using AzerothCoreControl.Core.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public sealed record PullResult(bool Success, string Message, bool RebuildRecommended, bool SqlChanged);

/// <summary><paramref name="BackupPath"/> is where the replaced folder was moved, so the user can recover it.</summary>
public sealed record RecloneResult(bool Success, string Message, string? BackupPath = null);

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
    /// Replace a non-git module folder with a proper git clone of <paramref name="cloneUrl"/>, so it can be
    /// update-checked from then on.
    /// </summary>
    /// <remarks>
    /// The existing folder is MOVED aside, never deleted: it may hold local edits, and this app has no way to
    /// know. If the clone fails, the original is put back, so a failed re-clone can't leave the user with no
    /// module at all.
    /// <para>
    /// The backup goes OUTSIDE <c>modules/</c>, into a sibling <c>module-backups/</c>. AzerothCore's
    /// modules/CMakeLists.txt adds every subdirectory it finds, so a backup left in there would still carry a
    /// CMakeLists.txt, declare the same module a second time, and break the very rebuild this operation tells
    /// the user to run. It would also show up as a bogus module row.
    /// </para>
    /// </remarks>
    public RecloneResult Reclone(string modulePath, string cloneUrl, string? timestamp = null)
    {
        var name = Path.GetFileName(modulePath);

        if (Repository.IsValid(modulePath))
            return new RecloneResult(false, $"{name} is already a git repository — use Pull instead.");
        if (!Directory.Exists(modulePath))
            return new RecloneResult(false, $"{modulePath} does not exist.");

        var stamp = timestamp ?? DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = BackupPathFor(modulePath, stamp);
        if (backupPath == null)
            return new RecloneResult(false, $"Could not work out where to keep a backup of {name}.");
        if (Directory.Exists(backupPath))
            return new RecloneResult(false, $"{backupPath} already exists — move it out of the way first.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            Directory.Move(modulePath, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RecloneResult(false, $"Could not move the existing folder aside: {ex.Message}");
        }

        try
        {
            Repository.Clone(cloneUrl, modulePath);
            _log.LogInformation("Re-cloned {Name} from {Url}; previous folder kept at {Backup}", name, cloneUrl, backupPath);
            return new RecloneResult(true,
                $"Re-cloned {name} from {cloneUrl}. Your previous folder is at module-backups/{Path.GetFileName(backupPath)} — " +
                "rebuild to apply, then delete it once you're happy.",
                backupPath);
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            if (TryRestore(backupPath, modulePath, out var restoreError))
            {
                _log.LogWarning(ex, "Re-clone of {Name} failed; original restored", name);
                return new RecloneResult(false, $"Clone failed, original restored: {ex.Message}");
            }

            _log.LogError("Re-clone of {Name} failed AND the original could not be restored: {Error}", name, restoreError);
            return new RecloneResult(false,
                $"Clone failed ({ex.Message}) and the original could not be moved back — it is safe at {backupPath}.",
                backupPath);
        }
    }

    /// <summary>
    /// Where to keep the replaced folder: <c>&lt;source&gt;/module-backups/&lt;name&gt;.backup-&lt;stamp&gt;</c>.
    /// A sibling of <c>modules/</c> — outside CMake's subdirectory sweep, but on the same volume, so moving
    /// there is a rename rather than a copy of the whole tree.
    /// </summary>
    internal static string? BackupPathFor(string modulePath, string stamp)
    {
        var name = Path.GetFileName(modulePath);
        var modulesDir = Path.GetDirectoryName(modulePath);
        var sourceDir = modulesDir == null ? null : Path.GetDirectoryName(modulesDir);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(sourceDir))
            return null;

        return Path.Combine(sourceDir, "module-backups", $"{name}.backup-{stamp}");
    }

    /// <summary>
    /// Move <paramref name="backupPath"/> back to <paramref name="modulePath"/>, clearing the failed clone.
    /// </summary>
    /// <remarks>
    /// Retried: Windows only tombstones a directory until the last handle closes, so a Delete can return while
    /// the entry still exists (an AV scanner or Explorer holding the half-written clone), and the Move that
    /// follows then fails — stranding the user's folder under a name they never chose. A few short retries
    /// cover the window in which the scanner lets go.
    /// </remarks>
    private static bool TryRestore(string backupPath, string modulePath, out string? error)
    {
        error = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(modulePath))
                    Directory.Delete(modulePath, recursive: true);
                Directory.Move(backupPath, modulePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                Thread.Sleep(250);
            }
        }
        return false;
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
