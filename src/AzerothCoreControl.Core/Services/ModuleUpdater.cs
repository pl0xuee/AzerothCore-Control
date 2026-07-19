using AzerothCoreControl.Core.Models;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

/// <summary>Why a pull was refused — the caller needs this to decide whether replacing the module is sane.</summary>
public enum PullFailureKind
{
    /// <summary>The pull succeeded.</summary>
    None,

    /// <summary>Uncommitted local changes. Re-cloning would fix it, at the cost of those changes.</summary>
    DirtyTree,

    /// <summary>Local commits the remote doesn't have. Re-cloning would fix it, at the cost of those commits.</summary>
    Diverged,

    /// <summary>
    /// Anything else: no network, TLS failure, bad credentials, rate limiting, a locked or corrupt .git.
    /// </summary>
    /// <remarks>
    /// These are TRANSIENT or environmental — the module's contents aren't the problem, so replacing it would
    /// destroy a perfectly good checkout without fixing anything. Must never trigger a destructive recovery.
    /// </remarks>
    Other,
}

public sealed record PullResult(
    bool Success,
    string Message,
    bool RebuildRecommended,
    bool SqlChanged,
    PullFailureKind Failure = PullFailureKind.None)
{
    /// <summary>
    /// Whether replacing the module with a fresh clone would actually resolve this. True only for local
    /// divergence — never for a failure that says nothing about the checkout's contents.
    /// </summary>
    public bool ReplacingWouldHelp => Failure is PullFailureKind.DirtyTree or PullFailureKind.Diverged;
}

/// <summary><paramref name="BackupPath"/> is where the replaced folder was moved, so the user can recover it.</summary>
public sealed record RecloneResult(bool Success, string Message, string? BackupPath = null);

/// <summary>
/// Outcome of repointing a checkout at a different remote.
/// </summary>
/// <param name="CanFastForward">A normal pull will now bring the module up to date.</param>
/// <param name="Diverged">
/// The new remote's history is not a superset of the local one, so a fast-forward pull cannot work. Common
/// when switching to a fork that has its own commits: nothing is broken, but getting onto it needs a re-clone
/// or a manual reset, which this app will not do behind the user's back.
/// </param>
public sealed record RepointResult(
    bool Success,
    string Message,
    bool CanFastForward = false,
    bool Diverged = false);

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
                return new PullResult(false, "Working tree has uncommitted changes — resolve them before pulling.",
                    false, false, PullFailureKind.DirtyTree);

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
            return new PullResult(false, "Cannot fast-forward — local commits diverge from the remote.",
                false, false, PullFailureKind.Diverged);
        }
        catch (LibGit2SharpException ex)
        {
            _log.LogWarning(ex, "Pull failed for {Module}", Path.GetFileName(modulePath));
            // Deliberately Other: this bucket is network failures, auth, rate limits and locked repos. None of
            // them mean the checkout is wrong, so nothing here may authorise replacing it.
            return new PullResult(false, ex.Message, false, false, PullFailureKind.Other);
        }
    }

    /// <summary>
    /// Force a module onto its remote's latest code by re-cloning it, for when a pull can't get there.
    /// </summary>
    /// <remarks>
    /// A fast-forward pull refuses on a dirty tree or a diverged history, which leaves the module stuck on old
    /// code indefinitely — and if that old code doesn't compile, it blocks every other module too, since
    /// AzerothCore builds them all into one target.
    /// <para>
    /// This is deliberately destructive and must only run when the user has asked for it: local edits and
    /// local commits do not survive. They are not deleted, though — <see cref="Reclone"/> moves the whole
    /// folder into <c>module-backups/</c> first, so the old state is recoverable.
    /// </para>
    /// <para>
    /// The remote comes from the checkout itself, so this replaces the module with the latest of whatever it
    /// currently follows — repoint it first if that isn't the repo you want.
    /// </para>
    /// </remarks>
    public RecloneResult ForceReplace(string modulePath)
    {
        var name = Path.GetFileName(modulePath);
        if (!Repository.IsValid(modulePath))
            return new RecloneResult(false, $"{name} is not a git repository — nothing to read a remote from.");

        string? url;
        try
        {
            using var repo = new Repository(modulePath);
            url = (repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault())?.Url;
        }
        catch (LibGit2SharpException ex)
        {
            return new RecloneResult(false, $"Could not read {name}'s remote: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(url))
            return new RecloneResult(false, $"{name} has no remote to re-clone from.");

        // Keep the branch. Replacing is already destructive enough without also moving the user to whatever
        // the remote calls default — same remote, so the branch is guaranteed to exist there.
        var branch = CurrentBranch(modulePath);
        _log.LogInformation("Force-replacing {Module} from {Url} on {Branch}", name, url, branch ?? "(default)");
        return Reclone(modulePath, url!, replaceGitRepo: true, branch: branch);
    }

    /// <summary>
    /// Point an existing checkout's <c>origin</c> at <paramref name="cloneUrl"/> and fetch, so update checks
    /// and pulls follow the repo the user pinned rather than whatever it was originally cloned from.
    /// </summary>
    /// <remarks>
    /// This is the git-checkout counterpart to <see cref="Reclone"/>: the folder already has history worth
    /// keeping, so the remote is rewritten in place instead of the whole tree being replaced.
    /// <para>
    /// Nothing is merged, reset or deleted here — it only changes where the module looks and reports what it
    /// found. A fork almost always diverges from its upstream, and quietly resetting onto it would throw away
    /// commits the user may have made. Deciding that is the user's call, so this hands back
    /// <see cref="RepointResult.Diverged"/> and stops.
    /// </para>
    /// <para>
    /// If the fetch fails the original URL is put back: a remote that cannot be reached is worse than the one
    /// that was working, and a half-applied switch would leave the module unable to update at all.
    /// </para>
    /// </remarks>
    public RepointResult RepointRemote(string modulePath, string cloneUrl)
    {
        var name = Path.GetFileName(modulePath);
        if (!Repository.IsValid(modulePath))
            return new RepointResult(false, $"{name} is not a git repository — use Re-clone instead.");

        try
        {
            using var repo = new Repository(modulePath);

            // A dirty tree survives the switch, but the pull that follows would refuse anyway — better to say
            // so now than after the remote has changed under them.
            if (repo.RetrieveStatus(new StatusOptions()).IsDirty)
                return new RepointResult(false, "Working tree has uncommitted changes — resolve them before switching remotes.");

            var origin = repo.Network.Remotes["origin"];
            var previousUrl = origin?.Url;
            if (origin == null)
                repo.Network.Remotes.Add("origin", cloneUrl);
            else
                repo.Network.Remotes.Update("origin", r => r.Url = cloneUrl);

            try
            {
                var remote = repo.Network.Remotes["origin"];
                var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification).ToList();
                Commands.Fetch(repo, "origin", refSpecs, FetchOptionsWithCredentials(), logMessage: null);
            }
            catch (LibGit2SharpException ex)
            {
                if (previousUrl != null)
                    repo.Network.Remotes.Update("origin", r => r.Url = previousUrl);
                else
                    repo.Network.Remotes.Remove("origin");
                return new RepointResult(false, $"Could not fetch from {cloneUrl} — remote left unchanged. {ex.Message}");
            }

            _log.LogInformation("Repointed {Module} at {Url}", name, cloneUrl);
            return DescribeDivergence(repo, name, cloneUrl);
        }
        catch (LibGit2SharpException ex)
        {
            _log.LogWarning(ex, "Repoint failed for {Module}", name);
            return new RepointResult(false, ex.Message);
        }
    }

    /// <summary>Work out what the user can now do: fast-forward, nothing to do, or an unavoidable manual step.</summary>
    private static RepointResult DescribeDivergence(Repository repo, string name, string cloneUrl)
    {
        var local = repo.Head.Tip;
        // The branch of the same name on the new remote — the one a pull would target.
        var tracked = repo.Branches[$"origin/{repo.Head.FriendlyName}"]
            ?? repo.Branches["origin/master"]
            ?? repo.Branches["origin/main"];

        if (local == null || tracked?.Tip == null)
            return new RepointResult(true,
                $"{name} now points at {cloneUrl}, but its branch isn't on that remote — check for updates to see where it stands.");

        if (local.Sha == tracked.Tip.Sha)
            return new RepointResult(true, $"{name} now points at {cloneUrl} and is already up to date with it.");

        var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(local, tracked.Tip);

        // AheadBy/BehindBy are int? and are BOTH null when the two commits share no common ancestor — an
        // unrelated repo rather than a fork. Nothing can be counted, but "diverged, re-clone" is exactly the
        // right advice, so treat it as the deepest divergence rather than printing empty numbers.
        if (divergence.AheadBy is not { } ahead || divergence.BehindBy is not { } behind)
            return new RepointResult(true,
                $"{name} now points at {cloneUrl}, but the two share no common history — it looks like a " +
                "different project, not a fork. Re-clone the module if you're sure this is the repo you want.",
                Diverged: true);

        // Behind only: a plain fast-forward pull gets there.
        if (ahead == 0 && behind > 0)
            return new RepointResult(true,
                $"{name} now points at {cloneUrl} — {behind} commits behind it. Pull to apply.",
                CanFastForward: true);

        // Anything else means the histories have parted ways, which is the normal state of a fork.
        return new RepointResult(true,
            $"{name} now points at {cloneUrl}, but the histories have diverged " +
            $"({ahead} local commits it doesn't have, {behind} of its commits you don't). " +
            "A fast-forward pull can't cross that — re-clone the module to move onto this repo cleanly.",
            Diverged: true);
    }

    /// <summary>Fetch options carrying the configured GitHub token, so private forks work too.</summary>
    private FetchOptions FetchOptionsWithCredentials()
        => new() { CredentialsProvider = CredentialsProvider() };

    /// <summary>The configured GitHub token as git credentials, or null when none is set.</summary>
    private CredentialsHandler? CredentialsProvider()
    {
        var token = _settings().GitHub.Token;
        return string.IsNullOrWhiteSpace(token)
            ? null
            : (_, _, _) => new UsernamePasswordCredentials { Username = token, Password = string.Empty };
    }

    /// <summary>The checked-out branch of a repo, or null if it can't be read.</summary>
    private static string? CurrentBranch(string modulePath)
    {
        try
        {
            if (!Repository.IsValid(modulePath))
                return null;
            using var repo = new Repository(modulePath);
            var name = repo.Head.FriendlyName;
            return string.IsNullOrEmpty(name) || name == "(no branch)" ? null : name;
        }
        catch (LibGit2SharpException)
        {
            return null;
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
    /// <param name="replaceGitRepo">
    /// Allow replacing a real checkout, not just a ZIP install. Needed to move onto a fork whose history has
    /// diverged, where no pull can get there — the existing checkout is still moved aside, never deleted, so
    /// this stays recoverable.
    /// </param>
    /// <param name="branch">
    /// Branch to check out. Pass the one the module was already on when replacing a checkout: cloning without
    /// it silently lands on the remote's default branch, which can be a different codebase entirely (master vs
    /// main, or a pinned 3.3.5a-* branch), and the next build would compile something the user never chose.
    /// </param>
    public RecloneResult Reclone(
        string modulePath,
        string cloneUrl,
        string? timestamp = null,
        bool replaceGitRepo = false,
        string? branch = null)
    {
        var name = Path.GetFileName(modulePath);

        if (Repository.IsValid(modulePath) && !replaceGitRepo)
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
            // Credentials, so a private fork clones as well as it fetches — Pull and RepointRemote both pass
            // the token, and a re-clone that didn't would fail AFTER the folder had been moved aside.
            var options = new CloneOptions { FetchOptions = { CredentialsProvider = CredentialsProvider() } };
            if (!string.IsNullOrWhiteSpace(branch))
                options.BranchName = branch;

            Repository.Clone(cloneUrl, modulePath, options);

            // Name the branch: this is the step most likely to move the user somewhere they didn't intend.
            var landedOn = CurrentBranch(modulePath);
            _log.LogInformation("Re-cloned {Name} from {Url} on {Branch}; previous folder kept at {Backup}",
                name, cloneUrl, landedOn, backupPath);
            return new RecloneResult(true,
                $"Re-cloned {name} from {cloneUrl}" + (landedOn == null ? "" : $" on branch {landedOn}") +
                $". Your previous folder is at module-backups/{Path.GetFileName(backupPath)} — " +
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
