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
        bool worldWasRunning = _world.State == ServerState.Running;

        try
        {
            // 1. Warn + graceful shutdown (only if running and we're going to swap binaries).
            if (rebuild && (worldWasRunning || _auth.State == ServerState.Running))
            {
                Report(progress, UpdateStep.Warn, "Warning players and shutting down gracefully...");
                await _world.StopAsync(graceful: true, cancellationToken).ConfigureAwait(false);
                await _auth.StopAsync(graceful: true, cancellationToken).ConfigureAwait(false);
            }

            // 2. Backup before touching anything.
            if (s.Backup.BackupBeforeUpdate)
            {
                Report(progress, UpdateStep.Backup, "Backing up databases...");
                var backup = await _backup.BackupAsync(m => Report(progress, UpdateStep.Backup, m), cancellationToken).ConfigureAwait(false);
                if (!backup.Success)
                    return Fail(progress, UpdateStep.Backup, backup.Message);
            }

            // 3. Pull the module.
            Report(progress, UpdateStep.Pull, $"Pulling {Path.GetFileName(modulePath)}...");
            var pull = _moduleUpdater.Pull(modulePath);
            if (!pull.Success)
                return Fail(progress, UpdateStep.Pull, pull.Message);
            Report(progress, UpdateStep.Pull, pull.Message);
            if (pull.SqlChanged)
                Report(progress, UpdateStep.Pull, "Note: module SQL changed — enable DB auto-update or apply it before the next boot.");

            // 4. Rebuild if requested / recommended.
            DeployResult? deployResult = null;
            if (rebuild || pull.RebuildRecommended)
            {
                Report(progress, UpdateStep.Build, "Recompiling (this can take a while)...");
                var build = await _build.BuildAsync(line => Report(progress, UpdateStep.Build, line), cancellationToken).ConfigureAwait(false);
                if (!build.Success || build.BinaryOutputDir == null)
                    return Fail(progress, UpdateStep.Build, $"Build failed (exit {build.ExitCode}).");

                // 5. Deploy — copies binaries + .conf.dist, NEVER the user's .conf.
                if (string.IsNullOrWhiteSpace(runDir))
                    return Fail(progress, UpdateStep.Deploy, "Run/deploy directory is not configured.");
                Report(progress, UpdateStep.Deploy, "Deploying new binaries (preserving your .conf files)...");
                deployResult = _deploy.Deploy(build.BinaryOutputDir, runDir!);
                Report(progress, UpdateStep.Deploy,
                    $"Updated {deployResult.UpdatedBinaries.Count} binaries, {deployResult.UpdatedConfigTemplates.Count} templates; " +
                    $"preserved {deployResult.PreservedConfigs.Count} custom .conf files.");
            }

            // 6. Restart if it was running before.
            if (worldWasRunning && !string.IsNullOrWhiteSpace(runDir))
            {
                Report(progress, UpdateStep.Restart, "Restarting servers...");
                _auth.Start(Path.Combine(runDir!, ServerKind.Auth.ExecutableName()), workingDirectory: runDir);
                _world.Start(Path.Combine(runDir!, ServerKind.World.ExecutableName()), workingDirectory: runDir);
            }

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
