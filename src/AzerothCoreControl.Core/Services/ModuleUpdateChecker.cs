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
    private readonly ModuleCatalogue _catalogue;
    private readonly ILogger _log;

    public ModuleUpdateChecker(
        Func<AppSettings> settings,
        ModuleCatalogue? catalogue = null,
        ILogger<ModuleUpdateChecker>? logger = null)
    {
        _settings = settings;
        _catalogue = catalogue ?? new ModuleCatalogue(settings);
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

    /// <summary>
    /// List installed modules: every immediate subdirectory of <c>modules/</c>, git repo or not.
    /// </summary>
    /// <remarks>
    /// This deliberately does NOT filter to git repos. A module installed by unzipping a GitHub download —
    /// which is how plenty of AzerothCore modules get installed — has no .git folder, and filtering those out
    /// made them vanish from the Modules tab entirely, with nothing to explain why. They're listed now and
    /// <see cref="CheckOneAsync"/> reports what's wrong with them instead.
    /// </remarks>
    public IReadOnlyList<string> ListModulePaths()
    {
        var folder = ModulesFolder;
        if (folder == null) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateDirectories(folder)
                .OrderBy(Path.GetFileName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not enumerate modules in {Folder}", folder);
            return Array.Empty<string>();
        }
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

        // Listed but not a git repo — almost always a module installed from a ZIP download. There's no remote
        // to read, but the catalogue is a GitHub topic search whose repo names match module folder names, so
        // we can still identify where it came from and offer to re-clone it properly.
        if (!Repository.IsValid(modulePath))
            return await DescribeNonGitModuleAsync(name, modulePath, github, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Describe a module folder that isn't a git checkout, identifying it via the catalogue where possible.
    /// It can't be update-checked (there's no local commit to compare), but naming its upstream and latest
    /// release turns a dead end into something actionable.
    /// </summary>
    private async Task<ModuleStatus> DescribeNonGitModuleAsync(
        string name, string modulePath, GitHubClient github, CancellationToken cancellationToken)
    {
        CatalogueEntry? entry = null;
        try
        {
            entry = await _catalogue.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
        }
        // Octokit implements its request timeout with a linked CancellationTokenSource, so a slow GitHub
        // surfaces as TaskCanceledException — NOT ApiException. Only a cancellation the CALLER asked for
        // should propagate; anything else is just a failed lookup and must not sink the whole check.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Catalogue lookup failed for {Name}", name);
        }

        if (entry == null)
        {
            return new ModuleStatus
            {
                Name = name,
                Path = modulePath,
                IsGitRepo = false,
                Error = "not a git repository, and no catalogue match — install it with git clone to enable update checks",
            };
        }

        // Best-effort: the latest release is a useful "what's current upstream", but plenty of modules
        // publish none, and its absence must not turn into an error.
        string? latestRelease = null;
        try
        {
            var owner = entry.FullName.Split('/')[0];
            var release = await github.Repository.Release.GetLatest(owner, entry.Name).ConfigureAwait(false);
            latestRelease = release?.TagName;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // No releases, rate-limited, offline, or a request timeout (which Octokit raises as
            // TaskCanceledException, not ApiException) — the identification below still stands either way.
        }

        return new ModuleStatus
        {
            Name = name,
            Path = modulePath,
            IsGitRepo = false,
            IdentifiedFromCatalogue = true,
            GitHubRepo = entry.FullName,
            CloneUrl = entry.CloneUrl,
            LatestReleaseTag = latestRelease,
            Error = "not a git repository — re-clone it to enable update checks",
        };
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
