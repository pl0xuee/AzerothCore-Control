using System.Reflection;
using AzerothCoreControl.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;

namespace AzerothCoreControl.Core.Services;

/// <summary>Information about the newest GitHub release of a repository.</summary>
public sealed record ReleaseInfo(
    string TagName,
    string Name,
    string HtmlUrl,
    string? Body,
    DateTimeOffset PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets)
{
    public bool IsNewerThan(string? currentVersion)
        => VersionCompare.IsNewer(TagName, currentVersion);
}

public sealed record ReleaseAsset(string Name, string DownloadUrl, long SizeBytes);

/// <summary>
/// Fetches GitHub Releases for a repo. Backs the "Check for updates" button — used both to self-update
/// this control app and to surface tagged releases of individual modules.
/// </summary>
public sealed class GitHubReleaseService
{
    private readonly Func<AppSettings> _settings;
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public GitHubReleaseService(Func<AppSettings> settings, HttpClient? http = null, ILogger<GitHubReleaseService>? logger = null)
    {
        _settings = settings;
        _http = http ?? new HttpClient();
        _log = logger ?? NullLogger<GitHubReleaseService>.Instance;
    }

    /// <summary>Version of the running control app, from the assembly's informational version.</summary>
    public static string CurrentAppVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Get the latest release of <c>owner/repo</c>, or null if the repo has none.</summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        try
        {
            var release = await client.Repository.Release.GetLatest(owner, repo).ConfigureAwait(false);
            return Map(release);
        }
        catch (Octokit.NotFoundException)
        {
            // No "latest" (e.g. only pre-releases) — fall back to the most recent of all releases.
            var all = await client.Repository.Release.GetAll(owner, repo, new ApiOptions { PageCount = 1, PageSize = 1 }).ConfigureAwait(false);
            var newest = all.FirstOrDefault();
            return newest == null ? null : Map(newest);
        }
        catch (ApiException ex)
        {
            _log.LogWarning(ex, "Failed to fetch releases for {Owner}/{Repo}", owner, repo);
            return null;
        }
    }

    /// <summary>Check whether the control app itself has a newer release than what's running.</summary>
    public async Task<ReleaseInfo?> CheckForAppUpdateAsync(CancellationToken cancellationToken = default)
    {
        var repo = _settings().GitHub.AppReleaseRepo;
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
            return null;
        var parts = repo.Split('/', 2);
        var release = await GetLatestReleaseAsync(parts[0], parts[1], cancellationToken).ConfigureAwait(false);
        return release != null && release.IsNewerThan(CurrentAppVersion) ? release : null;
    }

    /// <summary>Download a release asset to <paramref name="destinationPath"/>.</summary>
    public async Task DownloadAssetAsync(ReleaseAsset asset, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("AzerothCoreControl");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.SizeBytes;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var dest = File.Create(destinationPath);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            read += n;
            if (total > 0)
                progress?.Report((double)read / total);
        }
    }

    private GitHubClient CreateClient()
    {
        var client = new GitHubClient(new ProductHeaderValue("AzerothCoreControl"));
        var token = _settings().GitHub.Token;
        if (!string.IsNullOrWhiteSpace(token))
            client.Credentials = new Octokit.Credentials(token);
        return client;
    }

    private static ReleaseInfo Map(Release r) => new(
        r.TagName,
        string.IsNullOrWhiteSpace(r.Name) ? r.TagName : r.Name,
        r.HtmlUrl,
        r.Body,
        r.PublishedAt ?? r.CreatedAt,
        r.Assets.Select(a => new ReleaseAsset(a.Name, a.BrowserDownloadUrl, a.Size)).ToList());
}

/// <summary>Loose semantic-version comparison tolerant of a leading "v" and non-numeric tags.</summary>
public static class VersionCompare
{
    public static bool IsNewer(string candidateTag, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
            return true;
        if (TryParse(candidateTag, out var a) && TryParse(current, out var b))
            return a > b;
        // Non-semver tags: treat any difference as "newer" so the user is at least prompted.
        return !string.Equals(Normalize(candidateTag), Normalize(current), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string s) => s.Trim().TrimStart('v', 'V');

    private static bool TryParse(string s, out Version version)
    {
        var cleaned = Normalize(s);
        // Drop any pre-release/build suffix after a '-' or '+'.
        var cut = cleaned.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) cleaned = cleaned[..cut];
        return Version.TryParse(cleaned, out version!);
    }
}
