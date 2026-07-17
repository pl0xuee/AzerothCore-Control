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
    /// Find the catalogue entry for a module directory name (e.g. "mod-transmog"). Matching is by repo name,
    /// which is what makes this work without any git metadata.
    /// </summary>
    public async Task<CatalogueEntry?> ResolveAsync(string folderName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return null;

        var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(all, folderName);
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
