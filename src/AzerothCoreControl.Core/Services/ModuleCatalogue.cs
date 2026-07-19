using AzerothCoreControl.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;

namespace AzerothCoreControl.Core.Services;

/// <summary>One module as published in the AzerothCore catalogue.</summary>
public sealed record CatalogueEntry(
    string Name,
    string FullName,
    string CloneUrl,
    string? Description,
    int Stars,
    bool Archived);

/// <summary>
/// The AzerothCore module catalogue (azerothcore.org/catalogue).
/// </summary>
/// <remarks>
/// The catalogue is not a curated list — it is a GitHub topic search, which is why this queries GitHub
/// directly rather than scraping the site. That matters for modules installed from a ZIP download: they have
/// no git remote to read, but their folder name matches the upstream repo name, so the catalogue can still
/// identify where they came from.
/// </remarks>
public sealed class ModuleCatalogue
{
    /// <summary>The GitHub topic that defines the catalogue's module list.</summary>
    public const string ModuleTopic = "azerothcore-module";

    /// <summary>
    /// Modules whose best-known home is NOT the repo the catalogue finds — because the original has stopped
    /// being maintained and a fork carries it on.
    /// </summary>
    /// <remarks>
    /// The catalogue matches on repo name and ranks by popularity, so it keeps pointing at the original long
    /// after it stops compiling: the fork that actually works is less starred and often named identically.
    /// A user who follows the original sees "already up to date" forever while their build fails, with nothing
    /// in the app connecting the two.
    /// <para>
    /// Entries are a judgement call and must be justified in a comment — this is the app telling users where
    /// their code should come from, which it has no business doing casually. A user's own pin always wins.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, string> MaintainedForks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ZhengPeiRu21/mod-challenge-modes still declares OnPlayerResurrect's third parameter by value,
            // which stopped overriding the core hook when it became bool&. It has not been touched since
            // 2025-11-25, so every install following it fails the whole modules target — they all compile into
            // one. AldebaraanMKII's fork carries the fix plus later core-compatibility work.
            ["mod-challenge-modes"] = "AldebaraanMKII/mod-challenge-modes",
        };

    /// <summary>Enough for the ~300 published modules, with headroom; a guard against paging forever.</summary>
    private const int MaxPages = 6;
    private const int PerPage = 100;

    /// <summary>
    /// The catalogue changes on the order of weeks, and GitHub's search API allows only 10 requests/minute
    /// unauthenticated — a refresh costs several. Cache hard.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// How long to wait before retrying after a failed fetch. Without this, a check with several ZIP modules
    /// re-runs the whole multi-page fetch per module — so being rate-limited would make the app hammer the
    /// search API harder and deepen the very limit it just hit.
    /// </summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(2);

    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<CatalogueEntry>? _cache;
    private DateTimeOffset _cachedAt;
    private DateTimeOffset _retryNotBefore;

    public ModuleCatalogue(Func<AppSettings> settings, TimeProvider? time = null, ILogger<ModuleCatalogue>? logger = null)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger<ModuleCatalogue>.Instance;
    }

    /// <summary>Every module in the catalogue. Cached; returns an empty list if GitHub can't be reached.</summary>
    public async Task<IReadOnlyList<CatalogueEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_cache != null && now - _cachedAt < CacheTtl)
                return _cache;

            // A recent failure: don't retry yet. Serve the stale list if we have one.
            if (now < _retryNotBefore)
                return _cache ?? Array.Empty<CatalogueEntry>();

            var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
            if (fetched == null)
            {
                _retryNotBefore = now + FailureBackoff;
                return _cache ?? Array.Empty<CatalogueEntry>(); // offline: keep serving a stale list if we have one
            }

            _cache = fetched;
            _cachedAt = now;
            _retryNotBefore = default;
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Find the entry for a module directory name (e.g. "mod-transmog"). A configured override wins outright;
    /// otherwise this matches the catalogue by repo name, which is what makes it work without git metadata.
    /// </summary>
    public async Task<CatalogueEntry?> ResolveAsync(string folderName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return null;

        // An override is the user telling us which repo this actually is. It beats a name guess every time,
        // and it must not need a network round-trip to be honoured.
        if (FindOverride(_settings().ModuleRepoOverrides, folderName) is { } pinned)
            return pinned;

        // A known-maintained fork beats the catalogue's popularity match, which would send the user to an
        // original that no longer builds. Still below the user's own pin: they may be on that original
        // deliberately.
        if (FindMaintainedFork(folderName) is { } fork)
            return fork;

        var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(all, folderName);
    }

    /// <summary>The built-in maintained fork for <paramref name="folderName"/>, or null if there isn't one.</summary>
    internal static CatalogueEntry? FindMaintainedFork(string folderName)
        => MaintainedForks.TryGetValue(folderName.Trim(), out var repo)
            ? FromRepoSpec(folderName, repo)
            : null;

    /// <summary>The override for <paramref name="folderName"/> as an entry, or null if there isn't a usable one.</summary>
    internal static CatalogueEntry? FindOverride(IEnumerable<ModuleRepoOverride>? overrides, string folderName)
    {
        var match = overrides?.FirstOrDefault(o =>
            string.Equals(o.Module?.Trim(), folderName, StringComparison.OrdinalIgnoreCase));
        return match == null ? null : FromRepoSpec(folderName, match.Repository);
    }

    /// <summary>
    /// Turn "owner/repo" or a GitHub URL into an entry. Returns null for anything unparseable, so a typo in
    /// settings degrades to "we don't know where this came from" rather than a bogus clone URL.
    /// </summary>
    internal static CatalogueEntry? FromRepoSpec(string folderName, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;

        var text = spec.Trim();
        string owner, repo;

        if (text.Contains("://", StringComparison.Ordinal) || text.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var (parsedOwner, parsedRepo) = ModuleUpdateChecker.ParseGitHubUrl(text);
            if (parsedOwner == null || parsedRepo == null)
                return null;
            owner = parsedOwner;
            repo = parsedRepo;
        }
        else
        {
            // Bare "owner/repo".
            var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                return null;
            owner = parts[0];
            repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        }

        var fullName = $"{owner}/{repo}";
        // Name is the REPO name, not the folder name — callers use it for the GitHub release lookup, and an
        // override may legitimately point a folder at a differently-named repo (a fork with a suffix).
        return new CatalogueEntry(
            Name: repo,
            FullName: fullName,
            CloneUrl: $"https://github.com/{fullName}.git",
            Description: null,
            Stars: 0,
            Archived: false);
    }

    /// <summary>
    /// Pick the best entry whose repo name equals <paramref name="folderName"/>. Forks carry the topic too,
    /// so a name can legitimately match several repos — prefer a non-archived one, then the most-starred,
    /// which is reliably the canonical upstream rather than someone's stale fork.
    /// </summary>
    internal static CatalogueEntry? Resolve(IReadOnlyList<CatalogueEntry> all, string folderName) =>
        all.Where(e => string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Archived)
            .ThenByDescending(e => e.Stars)
            .FirstOrDefault();

    /// <summary>Returns null (rather than throwing) when GitHub is unreachable or rate-limited.</summary>
    private async Task<IReadOnlyList<CatalogueEntry>?> FetchAsync(CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var entries = new List<CatalogueEntry>();

        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new SearchRepositoriesRequest($"topic:{ModuleTopic}")
                {
                    PerPage = PerPage,
                    Page = page,
                };
                var result = await client.Search.SearchRepo(request).ConfigureAwait(false);
                if (result?.Items == null || result.Items.Count == 0)
                    break;

                foreach (var item in result.Items)
                {
                    entries.Add(new CatalogueEntry(
                        item.Name,
                        item.FullName,
                        item.CloneUrl,
                        item.Description,
                        item.StargazersCount,
                        item.Archived));
                }

                if (entries.Count >= result.TotalCount)
                    break;
            }

            _log.LogInformation("Catalogue: {Count} modules with topic {Topic}", entries.Count, ModuleTopic);
            return entries;
        }
        // Only a cancellation the caller asked for is a cancellation. Octokit's own request timeout arrives as
        // TaskCanceledException, and letting that through would break the Modules tab on a slow connection.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Rate limit, offline, DNS, timeout — the catalogue is an enhancement; the Modules tab must still
            // work without it.
            _log.LogWarning(ex, "Could not fetch the module catalogue");
            return null;
        }
    }

    private GitHubClient CreateClient()
    {
        var client = new GitHubClient(new ProductHeaderValue("AzerothCoreControl"));
        var token = _settings().GitHub.Token;
        if (!string.IsNullOrWhiteSpace(token))
            client.Credentials = new Credentials(token);
        return client;
    }
}
