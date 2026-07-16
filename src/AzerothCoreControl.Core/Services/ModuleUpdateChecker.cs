using System.Text.RegularExpressions;
using AzerothCoreControl.Core.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
// Both libraries define a `Repository` type; in this file it always means the local git repo.
using Repository = LibGit2Sharp.Repository;

namespace AzerothCoreControl.Core.Services;

/// <summary>
/// Scans the AzerothCore <c>modules/</c> folder and reports which module git repos have updates
/// available on their GitHub remote. Uses LibGit2Sharp for local repo state and Octokit for the
/// remote tip / latest release.
/// </summary>
public sealed partial class ModuleUpdateChecker
{
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;

    public ModuleUpdateChecker(Func<AppSettings> settings, ILogger<ModuleUpdateChecker>? logger = null)
    {
        _settings = settings;
        _log = logger ?? NullLogger<ModuleUpdateChecker>.Instance;
    }

    /// <summary>Absolute path to the modules folder, or null if the source dir isn't configured/doesn't have one.</summary>
    public string? ModulesFolder
    {
        get
        {
            var src = _settings().SourceDirectory;
            if (string.IsNullOrWhiteSpace(src)) return null;
            var path = Path.Combine(src, "modules");
            return Directory.Exists(path) ? path : null;
        }
    }

    /// <summary>List installed modules (immediate subdirectories of <c>modules/</c> that are git repos).</summary>
    public IReadOnlyList<string> ListModulePaths()
    {
        var folder = ModulesFolder;
        if (folder == null) return Array.Empty<string>();
        return Directory.EnumerateDirectories(folder)
            .Where(d => Repository.IsValid(d))
            .OrderBy(Path.GetFileName)
            .ToList();
    }

    /// <summary>Compute update status for every installed module.</summary>
    public async Task<IReadOnlyList<ModuleStatus>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var github = CreateGitHubClient();
        var results = new List<ModuleStatus>();
        foreach (var path in ListModulePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await CheckOneAsync(path, github, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    public async Task<ModuleStatus> CheckOneAsync(string modulePath, GitHubClient? github = null, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(modulePath);
        github ??= CreateGitHubClient();
        try
        {
            using var repo = new Repository(modulePath);
            var head = repo.Head;
            var branch = head.FriendlyName;
            var localSha = head.Tip?.Sha;
            var (owner, repoName) = ParseGitHubRemote(repo);

            string? remoteSha = null;
            string? latestRelease = null;
            int behind = 0, ahead = 0;
            IReadOnlyList<ModuleCommit> incoming = Array.Empty<ModuleCommit>();

            if (owner != null && repoName != null)
            {
                var branchToQuery = string.IsNullOrEmpty(branch) || branch == "(no branch)" ? "master" : branch;
                try
                {
                    var reference = await github.Git.Reference.Get(owner, repoName, $"heads/{branchToQuery}").ConfigureAwait(false);
                    remoteSha = reference.Object.Sha;
                }
                catch (Octokit.NotFoundException)
                {
                    // Branch name differs on remote (e.g. main vs master) — fall back to the default branch.
                    var repoInfo = await github.Repository.Get(owner, repoName).ConfigureAwait(false);
                    var reference = await github.Git.Reference.Get(owner, repoName, $"heads/{repoInfo.DefaultBranch}").ConfigureAwait(false);
                    remoteSha = reference.Object.Sha;
                }

                try
                {
                    var release = await github.Repository.Release.GetLatest(owner, repoName).ConfigureAwait(false);
                    latestRelease = release.TagName;
                }
                catch (Octokit.NotFoundException) { /* repo publishes no releases */ }

                if (localSha != null && remoteSha != null && localSha != remoteSha)
                {
                    // Ask GitHub how the two commits relate (ahead/behind counts + the incoming commits).
                    var comparison = await github.Repository.Commit
                        .Compare(owner, repoName, localSha, remoteSha).ConfigureAwait(false);
                    behind = comparison.AheadBy;   // commits on remote not in local == how far we're behind
                    ahead = comparison.BehindBy;   // commits on local not on remote

                    // comparison.Commits are the commits in remote that aren't local (oldest first) —
                    // reverse to newest-first for display.
                    incoming = comparison.Commits
                        .Reverse()
                        .Select(c => new ModuleCommit(
                            c.Sha.Length >= 7 ? c.Sha[..7] : c.Sha,
                            FirstLine(c.Commit.Message),
                            c.Commit.Author?.Name ?? c.Author?.Login ?? "unknown",
                            c.Commit.Author?.Date ?? c.Commit.Committer?.Date ?? default))
                        .ToList();
                }
            }

            var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = false });

            return new ModuleStatus
            {
                Name = name,
                Path = modulePath,
                Branch = branch,
                LocalCommit = Short(localSha),
                RemoteCommit = Short(remoteSha),
                GitHubRepo = owner != null ? $"{owner}/{repoName}" : null,
                BehindBy = behind,
                AheadBy = ahead,
                HasLocalChanges = status.IsDirty,
                LatestReleaseTag = latestRelease,
                IncomingCommits = incoming,
            };
        }
        catch (Exception ex) when (ex is RateLimitExceededException or ApiException or LibGit2SharpException)
        {
            _log.LogWarning(ex, "Failed to check module {Name}", name);
            return new ModuleStatus { Name = name, Path = modulePath, Error = ex.Message };
        }
    }

    private GitHubClient CreateGitHubClient()
    {
        var client = new GitHubClient(new ProductHeaderValue("AzerothCoreControl"));
        var token = _settings().GitHub.Token;
        if (!string.IsNullOrWhiteSpace(token))
            client.Credentials = new Octokit.Credentials(token);
        return client;
    }

    /// <summary>Extract "owner/repo" from the origin remote URL (https or ssh form).</summary>
    internal static (string? owner, string? repo) ParseGitHubRemote(Repository repo)
    {
        var origin = repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault();
        if (origin == null) return (null, null);
        return ParseGitHubUrl(origin.Url);
    }

    internal static (string? owner, string? repo) ParseGitHubUrl(string url)
    {
        // https://github.com/owner/repo(.git)  |  git@github.com:owner/repo(.git)
        var m = GitHubUrlRegex().Match(url);
        if (!m.Success) return (null, null);
        return (m.Groups["owner"].Value, m.Groups["repo"].Value);
    }

    private static string? Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha;

    /// <summary>First line of a commit message, trimmed.</summary>
    private static string FirstLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "(no message)";
        var idx = message.IndexOfAny(new[] { '\r', '\n' });
        return (idx >= 0 ? message[..idx] : message).Trim();
    }

    [GeneratedRegex(@"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrlRegex();
}
