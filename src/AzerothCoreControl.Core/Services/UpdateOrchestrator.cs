using AzerothCoreControl.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public enum UpdateStep { Warn, Backup, Pull, Build, Deploy, Restart }

public sealed record UpdateProgress(UpdateStep Step, string Message, bool IsError = false);

public sealed record UpdateReport(bool Success, string Message, DeployResult? Deploy = null);

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
    public async Task<UpdateReport> RunAsync(
        string modulePath,
        bool rebuild,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
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

        // A step that failed BEFORE any binary was touched leaves the installed server perfectly runnable —
        // so put it back up rather than leaving the realm down until someone notices the red text.
        UpdateReport FailAndRestore(UpdateStep step, string message)
        {
            RestartIfWeStopped();
            return Fail(progress, step, message);
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

            // 3. Pull the module.
            Report(progress, UpdateStep.Pull, $"Pulling {Path.GetFileName(modulePath)}...");
            var pull = _moduleUpdater.Pull(modulePath);
            if (!pull.Success)
                return FailAndRestore(UpdateStep.Pull, pull.Message);
            Report(progress, UpdateStep.Pull, pull.Message);
            if (pull.SqlChanged)
                Report(progress, UpdateStep.Pull, "Note: module SQL changed — enable DB auto-update or apply it before the next boot.");

            // 4. Rebuild if requested / recommended.
            DeployResult? deployResult = null;
            if (rebuild || pull.RebuildRecommended)
            {
                // The pull may have just told us a rebuild is needed when the caller didn't ask for one —
                // stop the servers before anything overwrites the binaries they're running from.
                await EnsureStoppedAsync().ConfigureAwait(false);

                Report(progress, UpdateStep.Build, "Recompiling (this can take a while)...");
                var build = await _build.BuildAsync(line => Report(progress, UpdateStep.Build, line), cancellationToken).ConfigureAwait(false);
                if (!build.Success || build.BinaryOutputDir == null)
                {
                    // Nothing was deployed, so the installed binaries are still the ones that worked.
                    return FailAndRestore(UpdateStep.Build, $"Build failed (exit {build.ExitCode}).");
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

            return new UpdateReport(true, "Update complete.", deployResult);
        }
        catch (OperationCanceledException)
        {
            return Fail(progress, UpdateStep.Restart, "Update cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update failed for {Module}", modulePath);
            return new UpdateReport(false, ex.Message);
        }
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
