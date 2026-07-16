using AzerothCoreControl.Core.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public sealed record PullResult(bool Success, string Message, bool RebuildRecommended, bool SqlChanged);

/// <summary>
/// Performs a fast-forward <c>git pull</c> on a module working directory using LibGit2Sharp.
/// Refuses to pull when the tree is dirty or a merge would be required, to avoid clobbering local edits.
/// </summary>
public sealed class ModuleUpdater
{
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;

    public ModuleUpdater(Func<AppSettings> settings, ILogger<ModuleUpdater>? logger = null)
    {
        _settings = settings;
        _log = logger ?? NullLogger<ModuleUpdater>.Instance;
    }

    public PullResult Pull(string modulePath)
    {
        try
        {
            using var repo = new Repository(modulePath);

            if (repo.RetrieveStatus(new StatusOptions()).IsDirty)
                return new PullResult(false, "Working tree has uncommitted changes — resolve them before pulling.", false, false);

            var beforeSha = repo.Head.Tip?.Sha;

            var token = _settings().GitHub.Token;
            var options = new PullOptions
            {
                FetchOptions = new FetchOptions
                {
                    CredentialsProvider = string.IsNullOrWhiteSpace(token)
                        ? null
                        : (_, _, _) => new UsernamePasswordCredentials { Username = token, Password = string.Empty },
                },
                MergeOptions = new MergeOptions { FastForwardStrategy = FastForwardStrategy.FastForwardOnly },
            };

            // LibGit2Sharp requires a signature even for FF-only pulls.
            var signature = new Signature("AzerothCoreControl", "azerothcorecontrol@localhost", DateTimeOffset.Now);
            var mergeResult = Commands.Pull(repo, signature, options);

            var afterSha = repo.Head.Tip?.Sha;
            var changed = beforeSha != afterSha;

            var (rebuild, sql) = changed
                ? InspectChanges(repo, beforeSha!, afterSha!)
                : (false, false);

            var message = mergeResult.Status switch
            {
                MergeStatus.UpToDate => "Already up to date.",
                MergeStatus.FastForward => $"Updated to {Short(afterSha)}.",
                _ => $"Pull result: {mergeResult.Status}.",
            };
            _log.LogInformation("Pulled {Module}: {Message}", Path.GetFileName(modulePath), message);
            return new PullResult(true, message, rebuild, sql);
        }
        catch (LibGit2SharpException ex) when (ex.Message.Contains("fast-forward", StringComparison.OrdinalIgnoreCase))
        {
            return new PullResult(false, "Cannot fast-forward — local commits diverge from the remote.", false, false);
        }
        catch (LibGit2SharpException ex)
        {
            _log.LogWarning(ex, "Pull failed for {Module}", Path.GetFileName(modulePath));
            return new PullResult(false, ex.Message, false, false);
        }
    }

    /// <summary>
    /// Look at the files touched by the pull to advise the user: C++/CMake changes → recompile needed;
    /// changed SQL under data/sql → database updates may need applying.
    /// </summary>
    private static (bool rebuild, bool sql) InspectChanges(Repository repo, string beforeSha, string afterSha)
    {
        bool rebuild = false, sql = false;
        var before = repo.Lookup<Commit>(beforeSha);
        var after = repo.Lookup<Commit>(afterSha);
        if (before == null || after == null)
            return (true, true); // be conservative if we can't diff

        var changes = repo.Diff.Compare<TreeChanges>(before.Tree, after.Tree);
        foreach (var change in changes)
        {
            var ext = Path.GetExtension(change.Path).ToLowerInvariant();
            if (ext is ".cpp" or ".h" or ".hpp" or ".cc" or ".c" or ".cmake" || change.Path.EndsWith("CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                rebuild = true;
            if (ext == ".sql")
                sql = true;
        }
        return (rebuild, sql);
    }

    private static string? Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha;
}
