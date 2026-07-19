using AzerothCoreControl.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public enum UpdateStep { Warn, Backup, Pull, Build, Deploy, Restart }

public sealed record UpdateProgress(UpdateStep Step, string Message, bool IsError = false);

/// <summary>How one module fared in the pull step of a batch update.</summary>
public sealed record ModulePullOutcome(string Name, bool Success, string Message);

public sealed record UpdateReport(
    bool Success,
    string Message,
    DeployResult? Deploy = null,
    IReadOnlyList<ModulePullOutcome>? Pulls = null)
{
    /// <summary>Per-module pull results, in the order they were attempted. Empty if we never got that far.</summary>
    public IReadOnlyList<ModulePullOutcome> Pulls { get; init; } = Pulls ?? Array.Empty<ModulePullOutcome>();
}

/// <summary>
/// Sequences a safe end-to-end module update:
/// warn players → graceful shutdown → DB backup → git pull → rebuild → config-preserving deploy → restart.
/// Any failing step aborts the sequence and reports the error; binaries are backed up by the deploy step
/// for rollback.
/// </summary>
public sealed class UpdateOrchestrator
{
    private readonly Func<AppSettings> _settings;
    private readonly ModuleUpdater _moduleUpdater;
    private readonly BuildService _build;
    private readonly DeployService _deploy;
    private readonly BackupService _backup;
    private readonly ServerProcessSupervisor _world;
    private readonly ServerProcessSupervisor _auth;
    private readonly ILogger _log;

    public UpdateOrchestrator(
        Func<AppSettings> settings,
        ModuleUpdater moduleUpdater,
        BuildService build,
        DeployService deploy,
        BackupService backup,
        ServerProcessSupervisor world,
        ServerProcessSupervisor auth,
        ILogger<UpdateOrchestrator>? logger = null)
    {
        _settings = settings;
        _moduleUpdater = moduleUpdater;
        _build = build;
        _deploy = deploy;
        _backup = backup;
        _world = world;
        _auth = auth;
        _log = logger ?? NullLogger<UpdateOrchestrator>.Instance;
    }

    /// <summary>
    /// Run a full "Pull + Build + Deploy" for one module.
    /// </summary>
    /// <param name="modulePath">Module working directory to update.</param>
    /// <param name="rebuild">Recompile after pulling (requires build tools + build dir).</param>
    /// <param name="progress">Streamed step-by-step progress.</param>
    public Task<UpdateReport> RunAsync(
        string modulePath,
        bool rebuild,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => RunAsync(new[] { modulePath }, rebuild, progress, cancellationToken);

    /// <summary>
    /// Update several modules as ONE operation: pull them all, then a single build → deploy → restart.
    /// </summary>
    /// <remarks>
    /// Running the single-module sequence once per module would be quadratically wasteful and unsafe: every
    /// module would trigger its own database backup, its own full recompile (AzerothCore builds all modules
    /// into one target regardless), and its own server bounce. Pulling everything first and compiling once is
    /// both far quicker and closer to what the user means by "update all".
    /// <para>
    /// A module whose pull fails does NOT abort the run — it simply stays at its current commit, which is a
    /// perfectly buildable state. With twenty modules installed, one with local edits shouldn't block the
    /// other nineteen. The run is only abandoned if EVERY pull failed, since then there is nothing new to
    /// build.
    /// </para>
    /// </remarks>
    /// <param name="modulePaths">Module working directories to update.</param>
    /// <param name="rebuild">Recompile after pulling (requires build tools + build dir).</param>
    /// <param name="progress">Streamed step-by-step progress.</param>
    /// <param name="replaceUnpullable">
    /// Re-clone any module whose pull is refused, replacing it with the remote's latest. DESTRUCTIVE — local
    /// edits and local commits are moved to a backup folder and no longer apply. Only pass this when the user
    /// has explicitly asked for it: without it a stuck module simply stays on its old code.
    /// </param>
    public async Task<UpdateReport> RunAsync(
        IReadOnlyList<string> modulePaths,
        bool rebuild,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool replaceUnpullable = false)
    {
        if (modulePaths.Count == 0)
            return new UpdateReport(false, "No modules selected to update.");

        var s = _settings();
        var runDir = s.DeployDirectory ?? s.RunDirectory;

        // Track each server independently: we must restart exactly what we stopped, no more and no less.
        var stoppedWorld = false;
        var stoppedAuth = false;
        var stoppedForUpdate = false;

        // Shutting down is idempotent and lazy: a pull can reveal that a rebuild is needed (RebuildRecommended)
        // after we've already decided not to stop, and binaries must never be swapped under a live server.
        async Task EnsureStoppedAsync()
        {
            if (stoppedForUpdate)
                return;

            // Read LIVE state, not a snapshot from the top of RunAsync: this can first run minutes later,
            // after a long backup, by which time an admin may have started a server that must not have its
            // binaries swapped out from under it.
            stoppedWorld = _world.State == ServerState.Running;
            stoppedAuth = _auth.State == ServerState.Running;

            // The flag means "we took servers down", so it stays false when there was nothing to take down —
            // otherwise the restart step announces a restart it isn't performing.
            if (!stoppedWorld && !stoppedAuth)
                return;
            stoppedForUpdate = true;

            Report(progress, UpdateStep.Warn, "Warning players and shutting down gracefully...");
            if (stoppedWorld)
                await _world.StopAsync(graceful: true, cancellationToken).ConfigureAwait(false);
            if (stoppedAuth)
                await _auth.StopAsync(graceful: true, cancellationToken).ConfigureAwait(false);
        }

        // Bring back exactly what we took down — never a server the user had deliberately stopped, and never
        // one we didn't stop (it's still running).
        void RestartIfWeStopped()
        {
            if (!stoppedForUpdate || string.IsNullOrWhiteSpace(runDir))
                return;
            Report(progress, UpdateStep.Restart, "Restarting servers...");
            if (stoppedAuth)
                _auth.Start(Path.Combine(runDir!, ServerKind.Auth.ExecutableName()), workingDirectory: runDir);
            if (stoppedWorld)
                _world.Start(Path.Combine(runDir!, ServerKind.World.ExecutableName()), workingDirectory: runDir);
        }

        // Filled by the pull step; declared out here so the failure paths can still report which modules had
        // already been pulled by the time a later step (build, deploy) gave up.
        var pulls = new List<ModulePullOutcome>(modulePaths.Count);

        // A step that failed BEFORE any binary was touched leaves the installed server perfectly runnable —
        // so put it back up rather than leaving the realm down until someone notices the red text.
        UpdateReport FailAndRestore(UpdateStep step, string message)
        {
            RestartIfWeStopped();
            return Fail(progress, step, message) with { Pulls = pulls };
        }

        try
        {
            // 1. Warn + graceful shutdown (only if we already know we're going to swap binaries).
            if (rebuild)
                await EnsureStoppedAsync().ConfigureAwait(false);

            // 2. Backup before touching anything.
            if (s.Backup.BackupBeforeUpdate)
            {
                Report(progress, UpdateStep.Backup, "Backing up databases...");
                var backup = await _backup.BackupAsync(m => Report(progress, UpdateStep.Backup, m), cancellationToken).ConfigureAwait(false);
                if (!backup.Success)
                    return FailAndRestore(UpdateStep.Backup, backup.Message);
            }

            // 3. Pull every module. One module's failure is not the batch's failure — see the remarks above.
            var rebuildRecommended = false;
            var sqlChanged = false;

            foreach (var path in modulePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                Report(progress, UpdateStep.Pull, $"Pulling {name}...");

                var pull = _moduleUpdater.Pull(path);

                // A refused pull leaves the module on old code forever. When the user has asked for it,
                // replace the folder outright with the remote's latest — the only way past a dirty tree or a
                // diverged history, and the old folder is kept as a backup.
                if (!pull.Success && replaceUnpullable)
                {
                    Report(progress, UpdateStep.Pull, $"{name}: {pull.Message} Replacing with the latest...");
                    var replaced = _moduleUpdater.ForceReplace(path);
                    pulls.Add(new ModulePullOutcome(name, replaced.Success, replaced.Message));
                    Report(progress, UpdateStep.Pull, $"{name}: {replaced.Message}");
                    if (replaced.Success)
                    {
                        // The whole tree was swapped, so assume the worst on both counts rather than diffing
                        // a history that no longer relates to what was there.
                        rebuildRecommended = true;
                        sqlChanged = true;
                    }
                    continue;
                }

                pulls.Add(new ModulePullOutcome(name, pull.Success, pull.Message));
                // Prefix the name: in a twenty-module run, a bare "Already up to date." says nothing about who.
                Report(progress, UpdateStep.Pull, $"{name}: {pull.Message}");

                if (!pull.Success)
                    continue;
                rebuildRecommended |= pull.RebuildRecommended;
                sqlChanged |= pull.SqlChanged;
            }

            var failedPulls = pulls.Where(p => !p.Success).ToList();
            // Nothing pulled cleanly, so there is nothing new to compile — don't spend twenty minutes
            // rebuilding the exact tree that is already installed.
            if (failedPulls.Count == pulls.Count)
                return FailAndRestore(UpdateStep.Pull, pulls.Count == 1
                    ? failedPulls[0].Message
                    : $"All {pulls.Count} modules failed to pull — nothing to build.");

            if (sqlChanged)
                Report(progress, UpdateStep.Pull, "Note: module SQL changed — enable DB auto-update or apply it before the next boot.");

            // 4. Rebuild if requested / recommended. One build covers every module: AzerothCore compiles them
            // all into a single target, so this is the same work whether one module changed or twenty.
            DeployResult? deployResult = null;
            if (rebuild || rebuildRecommended)
            {
                // The pull may have just told us a rebuild is needed when the caller didn't ask for one —
                // stop the servers before anything overwrites the binaries they're running from.
                await EnsureStoppedAsync().ConfigureAwait(false);

                Report(progress, UpdateStep.Build, "Recompiling (this can take a while)...");
                var build = await _build.BuildAsync(line => Report(progress, UpdateStep.Build, line), cancellationToken).ConfigureAwait(false);
                if (!build.Success || build.BinaryOutputDir == null)
                {
                    // Nothing was deployed, so the installed binaries are still the ones that worked.
                    return FailAndRestore(UpdateStep.Build, BuildFailureMessage(build.ExitCode, failedPulls));
                }

                // 5. Deploy — copies binaries + .conf.dist, NEVER the user's .conf.
                if (string.IsNullOrWhiteSpace(runDir))
                    return FailAndRestore(UpdateStep.Deploy, "Run/deploy directory is not configured.");
                Report(progress, UpdateStep.Deploy, "Deploying new binaries (preserving your .conf files)...");
                deployResult = _deploy.Deploy(build.BinaryOutputDir, runDir!);
                Report(progress, UpdateStep.Deploy,
                    $"Updated {deployResult.UpdatedBinaries.Count} binaries, {deployResult.UpdatedConfigTemplates.Count} templates; " +
                    $"preserved {deployResult.PreservedConfigs.Count} custom .conf files.");
            }

            // 6. Bring the servers back up, now running the new binaries.
            RestartIfWeStopped();

            // A partial batch still succeeded — but say so plainly rather than reporting a bare
            // "Update complete." over modules that were left behind.
            var message = failedPulls.Count == 0
                ? pulls.Count == 1 ? "Update complete." : $"Update complete — {pulls.Count} modules."
                : $"Update complete for {pulls.Count - failedPulls.Count} of {pulls.Count} modules; " +
                  $"{failedPulls.Count} could not be pulled ({string.Join(", ", failedPulls.Select(p => p.Name))}).";

            return new UpdateReport(true, message, deployResult, pulls);
        }
        catch (OperationCanceledException)
        {
            return Fail(progress, UpdateStep.Restart, "Update cancelled.") with { Pulls = pulls };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update failed for {Modules}", string.Join(", ", modulePaths.Select(Path.GetFileName)));
            return new UpdateReport(false, ex.Message, Pulls: pulls);
        }
    }

    /// <summary>
    /// What to say when the compile fails. Names the modules that didn't pull: they're still on their old
    /// code, which makes them the first thing to suspect when the build breaks straight afterwards.
    /// </summary>
    /// <remarks>
    /// A bare exit code sends the user hunting through compiler output for a cause the run already knew
    /// about — the batch deliberately builds on past a failed pull, so it owes them that connection.
    /// </remarks>
    internal static string BuildFailureMessage(int exitCode, IReadOnlyList<ModulePullOutcome> failedPulls)
    {
        var message = $"Build failed (exit {exitCode}).";
        if (failedPulls.Count == 0)
            return message;

        return message +
            $" {Describe(failedPulls)} still on the previous code after a failed pull — " +
            $"if the errors are in {(failedPulls.Count == 1 ? "it" : "one of them")}, that's why.";
    }

    /// <summary>"mod-a is" / "mod-a and mod-b are" / "mod-a, mod-b and mod-c are" — named, not counted.</summary>
    /// <remarks>
    /// A count alone ("2 modules failed to pull") is unusable: the whole point is to know WHICH module to go
    /// and look at.
    /// </remarks>
    private static string Describe(IReadOnlyList<ModulePullOutcome> failed)
    {
        var names = failed.Select(p => p.Name).ToList();
        var verb = names.Count == 1 ? "is" : "are";
        var list = names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
        };
        return $"{list} {verb}";
    }

    private static void Report(IProgress<UpdateProgress>? p, UpdateStep step, string message)
        => p?.Report(new UpdateProgress(step, message));

    private UpdateReport Fail(IProgress<UpdateProgress>? p, UpdateStep step, string message)
    {
        _log.LogError("Update aborted at {Step}: {Message}", step, message);
        p?.Report(new UpdateProgress(step, message, IsError: true));
        return new UpdateReport(false, message);
    }
}
