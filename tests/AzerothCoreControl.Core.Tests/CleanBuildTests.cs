using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

/// <summary>
/// The clean-rebuild option. Its whole promise is "recompile everything, keep my configs", so what it does
/// NOT touch matters as much as what it does.
/// </summary>
public class CleanBuildTests : IDisposable
{
    private readonly string _root;
    private readonly AppSettings _settings;

    private string BuildDir => Path.Combine(_root, "build");
    private string RunDir => Path.Combine(_root, "run");

    public CleanBuildTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acc-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(BuildDir);
        Directory.CreateDirectory(RunDir);
        _settings = new AppSettings
        {
            BuildDirectory = BuildDir,
            SourceDirectory = Path.Combine(_root, "source"),
            RunDirectory = RunDir,
        };
        _settings.Build.ReviewCMakeBeforeBuild = false;
        // A command that exists on every platform and fails immediately, so the build "runs" without cmake.
        _settings.Build.CMakePath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/false";
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ACleanBuild_ReRunsCMakeEvenWhenTheTreeIsAlreadyConfigured()
    {
        // The re-configure is the half that matters most: AzerothCore collects module sources with a glob at
        // configure time, so a module that gained or lost .cpp files leaves the generated build files on the
        // old list — compiling and linking cleanly while silently omitting the new code.
        File.WriteAllText(Path.Combine(BuildDir, "CMakeCache.txt"), "# already configured");

        var lines = new List<string>();
        await new BuildService(() => _settings).BuildAsync(lines.Add, clean: true);

        Assert.Contains(lines, l => l.StartsWith("[configure]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIncrementalBuild_DoesNotReConfigureAnAlreadyConfiguredTree()
    {
        // The counterpart: re-running CMake on every ordinary build would cost minutes for nothing.
        File.WriteAllText(Path.Combine(BuildDir, "CMakeCache.txt"), "# already configured");

        var lines = new List<string>();
        await new BuildService(() => _settings).BuildAsync(lines.Add, clean: false);

        Assert.DoesNotContain(lines, l => l.StartsWith("[configure]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("[build]", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyACleanBuild_PassesCleanFirst()
    {
        var clean = BuildService.BuildArgs(@"C:\build", parallelism: 0, clean: true);
        var incremental = BuildService.BuildArgs(@"C:\build", parallelism: 0, clean: false);

        Assert.Contains("--clean-first", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("--clean-first", incremental, StringComparison.Ordinal);
        // Everything else is unchanged: same targets, same configuration.
        Assert.Equal(incremental + " --clean-first", clean);
    }

    [Fact]
    public void TheBuildDirectoryIsQuoted_SoASpaceInThePathSurvives()
    {
        var args = BuildService.BuildArgs(@"C:\Program Files\azerothcore\build", parallelism: 4, clean: true);

        Assert.Contains("\"C:\\Program Files\\azerothcore\\build\"", args, StringComparison.Ordinal);
        Assert.Contains("--parallel 4", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACleanBuild_NeverTouchesTheRunDirectoryOrItsConfigs()
    {
        // The user's stated requirement. "Clean" reads as "wipes everything", but the build only ever works
        // inside the build directory — the live server folder is the deploy step's business.
        var conf = Path.Combine(RunDir, "worldserver.conf");
        File.WriteAllText(conf, "MaxPlayers = 500\nMy.Custom.Setting = 42");
        var before = File.ReadAllText(conf);

        await new BuildService(() => _settings).BuildAsync(clean: true);

        Assert.True(File.Exists(conf));
        Assert.Equal(before, File.ReadAllText(conf));
    }

    [Fact]
    public void DeployNeverWritesAUserConfig_HoweverTheBinariesWereBuilt()
    {
        // The other half of the promise: a clean rebuild produces fresh binaries, and deploying them must
        // still leave every hand-edited .conf byte-for-byte intact.
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "worldserver.exe"), "fresh binary");
        File.WriteAllText(Path.Combine(output, "worldserver.conf"), "PRISTINE DEFAULTS — must not be deployed");
        File.WriteAllText(Path.Combine(output, "worldserver.conf.dist"), "MaxPlayers = 100");

        var conf = Path.Combine(RunDir, "worldserver.conf");
        File.WriteAllText(conf, "MaxPlayers = 500\nMy.Custom.Setting = 42");

        var result = new DeployService().Deploy(output, RunDir);

        Assert.Equal("MaxPlayers = 500\nMy.Custom.Setting = 42", File.ReadAllText(conf));
        Assert.Contains("worldserver.conf", result.PreservedConfigs);
        Assert.Contains("worldserver.exe", result.UpdatedBinaries);
    }
}
