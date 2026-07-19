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
        // On by default, and it blocks on a cmake-gui window the test environment has no one to close.
        settings.Build.ReviewCMakeBeforeBuild = false;
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

    /// <summary>
    /// A checkout whose pull succeeds as "already up to date". It is cloned from a local upstream rather than
    /// merely init'd: a repo with no remote has nothing to pull from, and LibGit2Sharp treats that as an error.
    /// </summary>
    /// <returns>The root to delete, and the module checkout inside it.</returns>
    private static (string Root, string Module) AGitRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "acc-repo-" + Guid.NewGuid().ToString("N"));
        var upstream = Path.Combine(root, "upstream");
        Directory.CreateDirectory(upstream);

        LibGit2Sharp.Repository.Init(upstream);
        File.WriteAllText(Path.Combine(upstream, "README.md"), "module");
        using (var repo = new LibGit2Sharp.Repository(upstream))
        {
            LibGit2Sharp.Commands.Stage(repo, "*");
            var sig = new LibGit2Sharp.Signature("t", "t@t", DateTimeOffset.Now);
            repo.Commit("init", sig, sig, new LibGit2Sharp.CommitOptions());
        }

        var module = Path.Combine(root, "mod-good");
        LibGit2Sharp.Repository.Clone(upstream, module);
        return (root, module);
    }

    [Fact]
    public async Task WithNoModules_NothingHappens()
    {
        var h = Create();

        var report = await h.Orchestrator.RunAsync(Array.Empty<string>(), rebuild: true);

        Assert.False(report.Success);
        Assert.Empty(report.Pulls);
        Assert.Equal(0, h.WorldLauncher.LaunchCount);
    }

    [Fact]
    public async Task EveryModuleIsPulled_AndReportedIndividually()
    {
        // The batch must not stop at the first module: with twenty installed, the user needs to know which
        // ones moved and which didn't, not just that "an update failed".
        var h = Create();
        var a = NotAGitRepo();
        var b = NotAGitRepo();

        var report = await h.Orchestrator.RunAsync(new[] { a, b }, rebuild: true);

        Assert.Equal(2, report.Pulls.Count);
        Assert.Equal(Path.GetFileName(a), report.Pulls[0].Name);
        Assert.Equal(Path.GetFileName(b), report.Pulls[1].Name);
        Assert.All(report.Pulls, p => Assert.False(p.Success));
    }

    [Fact]
    public async Task WhenEveryPullFails_TheBuildIsSkipped()
    {
        // Nothing pulled cleanly, so the tree on disk is exactly what is already installed — spending twenty
        // minutes recompiling it would be pure waste.
        var h = Create();

        var report = await h.Orchestrator.RunAsync(new[] { NotAGitRepo(), NotAGitRepo() }, rebuild: true);

        Assert.False(report.Success);
        Assert.Contains("nothing to build", report.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneBadModule_DoesNotBlockTheRest()
    {
        // Regression guard for the whole point of the batch: a single module with local edits must not stop
        // the other nineteen from being updated and built.
        var h = Create();
        var (root, good) = AGitRepo();
        try
        {
            var report = await h.Orchestrator.RunAsync(new[] { NotAGitRepo(), good }, rebuild: true);

            // It reached the build step (which fails here — there is no cmake in the test environment), rather
            // than giving up at the first failed pull.
            Assert.Equal(2, report.Pulls.Count);
            Assert.True(report.Pulls[1].Success);
            Assert.DoesNotContain("nothing to build", report.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

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
