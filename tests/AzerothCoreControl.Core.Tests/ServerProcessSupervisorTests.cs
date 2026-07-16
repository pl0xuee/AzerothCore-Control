using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using AzerothCoreControl.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

public class ServerProcessSupervisorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static (ServerProcessSupervisor sup, FakeProcessLauncher launcher, List<SupervisorEvent> events, FakeTimeProvider time)
        CreateWorld(Action<WatchdogSettings>? configure = null, bool autoExitOnShutdown = false)
    {
        var settings = new AppSettings();
        // Zero backoff keeps restart behavioral tests deterministic (no timer to advance).
        settings.Watchdog.InitialBackoff = TimeSpan.Zero;
        settings.Watchdog.MaxBackoff = TimeSpan.Zero;
        configure?.Invoke(settings.Watchdog);

        var time = new FakeTimeProvider();
        var launcher = new FakeProcessLauncher { AutoExitOnShutdown = autoExitOnShutdown };
        var events = new List<SupervisorEvent>();
        var sup = new ServerProcessSupervisor(ServerKind.World, launcher, () => settings, time);
        sup.Notable += e => { lock (events) events.Add(e); };
        return (sup, launcher, events, time);
    }

    [Fact]
    public async Task Start_LaunchesProcess_AndReportsRunning()
    {
        var (sup, launcher, _, _) = CreateWorld();
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        Assert.Equal(ServerState.Running, sup.State);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task CleanShutdown_ExitCode0_AfterRunning_StaysStopped()
    {
        var (sup, launcher, events, time) = CreateWorld();
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        // Ran for a while, then a normal clean shutdown (exit 0).
        time.Advance(TimeSpan.FromMinutes(1));
        launcher.Last.SimulateExit(0); // SHUTDOWN_EXIT_CODE

        await AssertEventuallyAsync(() => sup.State == ServerState.Stopped);
        Assert.Equal(1, launcher.LaunchCount); // no restart
        Assert.Contains(events, e => e.Kind == SupervisorEventKind.CleanShutdown);
    }

    [Fact]
    public async Task ExitCode0_ImmediatelyAfterStart_ReportedAsStartupFailure()
    {
        var (sup, launcher, events, _) = CreateWorld();
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        // Exits cleanly but immediately (no time advanced) — treated as a probable startup problem.
        launcher.Last.SimulateExit(0);

        await AssertEventuallyAsync(() => sup.State == ServerState.Stopped);
        Assert.Equal(1, launcher.LaunchCount); // still no restart
        Assert.Contains(events, e => e.Kind == SupervisorEventKind.Crashed); // flagged, not "clean"
    }

    [Fact]
    public async Task RestartRequested_ExitCode1_Relaunches()
    {
        var (sup, launcher, events, _) = CreateWorld();
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        launcher.Last.SimulateExit(1); // RESTART_EXIT_CODE

        await launcher.WaitForLaunchCountAsync(2, Timeout);
        Assert.Contains(events, e => e.Kind == SupervisorEventKind.Restarting);
    }

    [Fact]
    public async Task Crash_NonZeroExit_RelaunchesWithAutoRestart()
    {
        var (sup, launcher, events, _) = CreateWorld();
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        launcher.Last.SimulateExit(139); // segfault-style crash

        await launcher.WaitForLaunchCountAsync(2, Timeout);
        Assert.Contains(events, e => e.Kind == SupervisorEventKind.Crashed);
    }

    [Fact]
    public async Task Crash_IncludesLastServerOutput_AsDiagnostic()
    {
        var (sup, launcher, events, _) = CreateWorld(w => w.AutoRestart = false);
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        launcher.Last.EmitOutput("Could not connect to the database.");
        launcher.Last.SimulateExit(3);

        await AssertEventuallyAsync(() => sup.State == ServerState.Crashed);
        Assert.Contains(events, e => e.Message.Contains("Could not connect to the database"));
    }

    [Fact]
    public async Task Crash_WithAutoRestartDisabled_DoesNotRelaunch()
    {
        var (sup, launcher, events, _) = CreateWorld(w => w.AutoRestart = false);
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        launcher.Last.SimulateExit(139);

        await AssertEventuallyAsync(() => sup.State == ServerState.Crashed);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task CrashLoop_TripsBreaker_AfterThreshold()
    {
        var (sup, launcher, events, _) = CreateWorld(w =>
        {
            w.CrashLoopThreshold = 3;
            w.CrashWindow = TimeSpan.FromMinutes(10);
        });

        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        // Crash #1 → relaunch (2), crash #2 → relaunch (3), crash #3 → breaker trips.
        launcher.Last.SimulateExit(139);
        await launcher.WaitForLaunchCountAsync(2, Timeout);
        launcher.Last.SimulateExit(139);
        await launcher.WaitForLaunchCountAsync(3, Timeout);
        launcher.Last.SimulateExit(139);

        await AssertEventuallyAsync(() => sup.State == ServerState.Crashed);
        Assert.Equal(3, launcher.LaunchCount); // no 4th launch
        Assert.Contains(events, e => e.Kind == SupervisorEventKind.CrashLoopTripped);
    }

    [Fact]
    public async Task UserStop_SuppressesRestart()
    {
        var (sup, launcher, _, _) = CreateWorld(autoExitOnShutdown: true);
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        await sup.StopAsync(graceful: true);

        Assert.Equal(ServerState.Stopped, sup.State);
        Assert.Equal(1, launcher.LaunchCount);
        // Graceful stop sends the drain command to the worldserver console.
        Assert.Contains(launcher.Last.StdinLines, l => l.StartsWith(".server shutdown"));
    }

    [Fact]
    public async Task StartImmediatelyAfterStop_Relaunches()
    {
        // Regression for the restart-after-stop race: Start right after StopAsync must actually relaunch,
        // not no-op on a stale Running state.
        var (sup, launcher, _, _) = CreateWorld(autoExitOnShutdown: true);
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        await sup.StopAsync(graceful: true);          // StopAsync must not return until state is Stopped
        Assert.Equal(ServerState.Stopped, sup.State);

        sup.Start("worldserver.exe");                 // should launch a second instance
        await launcher.WaitForLaunchCountAsync(2, Timeout);
        Assert.Equal(ServerState.Running, sup.State);
    }

    [Fact]
    public async Task SendConsole_WritesToStdin()
    {
        var (sup, launcher, _, _) = CreateWorld();
        sup.Start("worldserver.exe");
        await launcher.WaitForLaunchCountAsync(1, Timeout);

        sup.SendConsole(".account create test test");

        Assert.Contains(".account create test test", launcher.Last.StdinLines);
    }

    [Fact]
    public void Backoff_GrowsExponentially_AndCaps()
    {
        var wd = new WatchdogSettings
        {
            InitialBackoff = TimeSpan.FromSeconds(5),
            MaxBackoff = TimeSpan.FromSeconds(60),
        };

        Assert.Equal(TimeSpan.FromSeconds(5), ServerProcessSupervisor.ComputeBackoff(wd, 1));
        Assert.Equal(TimeSpan.FromSeconds(10), ServerProcessSupervisor.ComputeBackoff(wd, 2));
        Assert.Equal(TimeSpan.FromSeconds(20), ServerProcessSupervisor.ComputeBackoff(wd, 3));
        Assert.Equal(TimeSpan.FromSeconds(60), ServerProcessSupervisor.ComputeBackoff(wd, 10)); // capped
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                Assert.Fail("Condition not met within timeout.");
            await Task.Delay(10);
        }
    }
}
