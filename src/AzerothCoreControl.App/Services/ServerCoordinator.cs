using System.IO;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Process;
using AzerothCoreControl.Core.Services;
using Microsoft.Extensions.Logging;

namespace AzerothCoreControl.App.Services;

/// <summary>
/// Application-level composition root that owns the two server supervisors and every Core service,
/// wires their events to notifications, and exposes high-level operations to the view-models.
/// </summary>
public sealed class ServerCoordinator : IAsyncDisposable
{
    private readonly SettingsStore _store;
    private AppSettings _settings;

    public ServerCoordinator(SettingsStore store, ILoggerFactory loggerFactory)
    {
        _store = store;
        _settings = store.Load();

        var launcher = new SystemProcessLauncher();
        Func<AppSettings> accessor = () => _settings;

        World = new ServerProcessSupervisor(ServerKind.World, launcher, accessor,
            logger: loggerFactory.CreateLogger<ServerProcessSupervisor>());
        Auth = new ServerProcessSupervisor(ServerKind.Auth, launcher, accessor,
            logger: loggerFactory.CreateLogger<ServerProcessSupervisor>());

        Notifications = new NotificationService(accessor, logger: loggerFactory.CreateLogger<NotificationService>());
        MySql = new MySqlMonitor(accessor, loggerFactory.CreateLogger<MySqlMonitor>());
        ModuleChecker = new ModuleUpdateChecker(accessor, loggerFactory.CreateLogger<ModuleUpdateChecker>());
        ModuleUpdater = new ModuleUpdater(accessor, loggerFactory.CreateLogger<ModuleUpdater>());
        Releases = new GitHubReleaseService(accessor, logger: loggerFactory.CreateLogger<GitHubReleaseService>());
        Backup = new BackupService(accessor, logger: loggerFactory.CreateLogger<BackupService>());

        var build = new BuildService(accessor, loggerFactory.CreateLogger<BuildService>());
        var deploy = new DeployService(loggerFactory.CreateLogger<DeployService>());
        BuildSvc = build;
        DeploySvc = deploy;
        Orchestrator = new UpdateOrchestrator(accessor, ModuleUpdater, build, deploy, Backup, World, Auth,
            loggerFactory.CreateLogger<UpdateOrchestrator>());
        Schedule = new ScheduleService(accessor, World, Auth, Backup, logger: loggerFactory.CreateLogger<ScheduleService>());

        AppUpdater = new AppUpdater(this);

        // Route notable lifecycle events (crashes, breaker trips) to the notification sinks.
        World.Notable += OnNotable;
        Auth.Notable += OnNotable;

        Schedule.Start();
    }

    public AppUpdater AppUpdater { get; }

    public ServerProcessSupervisor World { get; }
    public ServerProcessSupervisor Auth { get; }
    public NotificationService Notifications { get; }
    public MySqlMonitor MySql { get; }
    public ModuleUpdateChecker ModuleChecker { get; }
    public ModuleUpdater ModuleUpdater { get; }
    public GitHubReleaseService Releases { get; }
    public BackupService Backup { get; }
    public BuildService BuildSvc { get; }
    public DeployService DeploySvc { get; }
    public UpdateOrchestrator Orchestrator { get; }
    public ScheduleService Schedule { get; }

    public AppSettings Settings => _settings;

    public void SaveSettings() => _store.Save(_settings);

    public void ReplaceSettings(AppSettings settings)
    {
        _settings = settings;
        _store.Save(settings);
    }

    /// <summary>Start MySQL (if required), then auth and world servers, from the configured run directory.</summary>
    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        var runDir = RequireRunDirectory();
        await EnsureMySqlAsync(cancellationToken).ConfigureAwait(false);
        StartOne(ServerKind.Auth, runDir);
        StartOne(ServerKind.World, runDir);
    }

    public async Task StopAllAsync(bool graceful = true, CancellationToken cancellationToken = default)
    {
        // Drain the world server first (players), then the auth server.
        await World.StopAsync(graceful, cancellationToken).ConfigureAwait(false);
        await Auth.StopAsync(graceful, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Start a single server (ensuring MySQL first if required).</summary>
    public async Task StartServerAsync(ServerKind kind, CancellationToken cancellationToken = default)
    {
        var runDir = RequireRunDirectory();
        await EnsureMySqlAsync(cancellationToken).ConfigureAwait(false);
        StartOne(kind, runDir);
    }

    private string RequireRunDirectory()
    {
        var runDir = _settings.RunDirectory;
        if (string.IsNullOrWhiteSpace(runDir))
            throw new InvalidOperationException("Run directory is not configured. Open Settings to set it.");
        if (!Directory.Exists(runDir))
            throw new DirectoryNotFoundException($"Run directory does not exist: {runDir}");
        return runDir;
    }

    private async Task EnsureMySqlAsync(CancellationToken cancellationToken)
    {
        if (!_settings.MySql.RequireForStart)
            return;
        var ok = await MySql.EnsureRunningAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (!ok && MySql.GetState() != MySqlState.NotConfigured)
            throw new InvalidOperationException("MySQL is not running and could not be started.");
    }

    private void StartOne(ServerKind kind, string runDir)
    {
        var exe = Path.Combine(runDir, kind.ExecutableName());
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"{kind.ExecutableName()} was not found in the run directory. Check the Run directory in Settings.", exe);

        var sup = kind == ServerKind.World ? World : Auth;

        // Already ours and alive — nothing to start. This check MUST come before the cleanup below: that
        // matches purely on exe path, so it cannot tell our own live server from an orphan and would kill
        // it outright (no player warning, no character save) while Start() silently no-ops on a running
        // supervisor. "Start all" with only one server down would take the other one with it.
        //
        // Only Running/Starting — deliberately NOT Restarting. In Restarting the process has already exited
        // and we're sitting out a crash backoff (up to 5 minutes); a manual start there means "bring it up
        // now", which Start() honours by cancelling the pending restart. Skipping would silently do nothing.
        if (sup.State is ServerState.Running or ServerState.Starting)
            return;

        // Clean up any orphaned instance of this exact server (e.g. left over from a prior restart storm)
        // so it doesn't hold the login/world port and make the fresh start fail to bind.
        var killed = ProcessCleanup.KillStaleInstances(exe);
        if (killed > 0)
            _ = Notifications.NotifyAsync(kind.DisplayName(),
                $"Cleaned up {killed} orphaned {kind.ExecutableName()} process(es) before starting.", NotificationSeverity.Info);

        sup.Start(exe, workingDirectory: runDir);
    }

    /// <summary>
    /// Stop a single server. For the world server this is always a SAFE shutdown — it sends
    /// <c>.server shutdown &lt;delay&gt;</c> so players are warned and characters are saved before exit.
    /// </summary>
    public Task StopServerAsync(ServerKind kind, CancellationToken cancellationToken = default)
        => (kind == ServerKind.World ? World : Auth).StopAsync(graceful: true, cancellationToken);

    private void OnNotable(SupervisorEvent e)
    {
        var severity = e.Kind switch
        {
            SupervisorEventKind.CrashLoopTripped => NotificationSeverity.Critical,
            SupervisorEventKind.Crashed => NotificationSeverity.Warning,
            _ => NotificationSeverity.Info,
        };
        // Fire-and-forget: notifications must never block the supervisor's state machine.
        _ = Notifications.NotifyAsync(e.Server.DisplayName(), e.Message, severity);
    }

    public async ValueTask DisposeAsync()
    {
        // Kill the child processes FIRST. These are synchronous and immediate, whereas draining the
        // scheduler can park on a job that cannot be cancelled — a multi-GB world dump being zipped runs for
        // minutes. Behind that await, any bounded wait on shutdown expires before the servers are killed and
        // they outlive the app, holding ports 3724/8085.
        World.Dispose();
        Auth.Dispose();
        await Schedule.DisposeAsync().ConfigureAwait(false);
    }
}
