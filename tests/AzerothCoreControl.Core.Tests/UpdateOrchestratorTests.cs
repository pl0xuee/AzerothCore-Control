using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using AzerothCoreControl.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

public class UpdateOrchestratorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed record Harness(
        UpdateOrchestrator Orchestrator,
        ServerProcessSupervisor World,
        ServerProcessSupervisor Auth,
        FakeProcessLauncher WorldLauncher,
        FakeProcessLauncher AuthLauncher);

    private static Harness Create()
    {
        var settings = new AppSettings
        {
            RunDirectory = Path.GetTempPath(),
        };
        settings.Backup.BackupBeforeUpdate = false; // not what these tests are about
        settings.Watchdog.InitialBackoff = TimeSpan.Zero;
        settings.Watchdog.MaxBackoff = TimeSpan.Zero;

        var time = new FakeTimeProvider();
        // The world server must honour ".server shutdown" by exiting, as the real one does — otherwise the
        // graceful drain waits on a FakeTimeProvider clock that no one advances, and the test hangs.
        var worldLauncher = new FakeProcessLauncher { AutoExitOnShutdown = true };
        var authLauncher = new FakeProcessLauncher { AutoExitOnShutdown = true };
        var world = new ServerProcessSupervisor(ServerKind.World, worldLauncher, () => settings, time);
        var auth = new ServerProcessSupervisor(ServerKind.Auth, authLauncher, () => settings, time);

        var orchestrator = new UpdateOrchestrator(
            () => settings,
            new ModuleUpdater(() => settings),
            new BuildService(() => settings),
            new DeployService(),
            new BackupService(() => settings, time),
            world,
            auth);

        return new Harness(orchestrator, world, auth, worldLauncher, authLauncher);
    }

    /// <summary>A path that is not a git repo, so the Pull step fails early and deterministically.</summary>
    private static string NotAGitRepo() => Path.Combine(Path.GetTempPath(), "acc-not-a-repo-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WhenOnlyAuthWasRunning_ItIsBroughtBackUp()
    {
        // Regression: the shutdown stopped BOTH servers if EITHER was running, but the restart only keyed off
        // the world server — so updating with world already stopped left authserver down for good, under a
        // report that said "Update complete."
        var h = Create();
        h.Auth.Start("authserver.exe");
        await h.AuthLauncher.WaitForLaunchCountAsync(1, Timeout);
        Assert.Equal(ServerState.Running, h.Auth.State);

        var report = await h.Orchestrator.RunAsync(NotAGitRepo(), rebuild: true);

        Assert.False(report.Success);
        await h.AuthLauncher.WaitForLaunchCountAsync(2, Timeout); // stopped for the update, then restarted
        Assert.Equal(0, h.WorldLauncher.LaunchCount);             // world was down and must stay down
    }

    [Fact]
    public async Task AServerTheUserStopped_IsNotStartedByAnUpdate()
    {
        // The mirror of the bug above: world running, auth deliberately stopped. The update must not decide
        // to "helpfully" start authserver.
        var h = Create();
        h.World.Start("worldserver.exe");
        await h.WorldLauncher.WaitForLaunchCountAsync(1, Timeout);

        var report = await h.Orchestrator.RunAsync(NotAGitRepo(), rebuild: true);

        Assert.False(report.Success);
        await h.WorldLauncher.WaitForLaunchCountAsync(2, Timeout);
        Assert.Equal(0, h.AuthLauncher.LaunchCount);
    }

    [Fact]
    public async Task AFailedUpdate_DoesNotLeaveTheRealmDown()
    {
        // Nothing was deployed, so the installed binaries still work — the servers must come back rather
        // than staying down until an admin notices the red text.
        var h = Create();
        h.World.Start("worldserver.exe");
        h.Auth.Start("authserver.exe");
        await h.WorldLauncher.WaitForLaunchCountAsync(1, Timeout);
        await h.AuthLauncher.WaitForLaunchCountAsync(1, Timeout);

        var report = await h.Orchestrator.RunAsync(NotAGitRepo(), rebuild: true);

        Assert.False(report.Success);
        await h.WorldLauncher.WaitForLaunchCountAsync(2, Timeout);
        await h.AuthLauncher.WaitForLaunchCountAsync(2, Timeout);
        Assert.Equal(ServerState.Running, h.World.State);
        Assert.Equal(ServerState.Running, h.Auth.State);
    }

    [Fact]
    public async Task WithNothingRunning_NothingIsStarted()
    {
        var h = Create();

        var report = await h.Orchestrator.RunAsync(NotAGitRepo(), rebuild: true);

        Assert.False(report.Success);
        Assert.Equal(0, h.WorldLauncher.LaunchCount);
        Assert.Equal(0, h.AuthLauncher.LaunchCount);
    }
}
