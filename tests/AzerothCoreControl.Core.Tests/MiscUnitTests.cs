using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

public class ExitCodePolicyTests
{
    [Theory]
    [InlineData(0, ExitClassification.CleanShutdown)]
    [InlineData(1, ExitClassification.RestartRequested)]
    [InlineData(139, ExitClassification.Crash)]
    [InlineData(-1, ExitClassification.Crash)]
    [InlineData(3, ExitClassification.Crash)]
    public void Classify_MapsExitCodes(int code, ExitClassification expected)
        => Assert.Equal(expected, ExitCodePolicy.Classify(code));
}

public class VersionCompareTests
{
    [Theory]
    [InlineData("v1.2.0", "1.1.0", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("v2.0.0", "v2.0.1", false)]
    [InlineData("1.0.0", null, true)]           // no current version → treat as newer
    [InlineData("v1.2.0-beta", "1.1.0", true)]  // pre-release suffix stripped
    [InlineData("release-b", "release-a", true)] // non-semver → any difference prompts
    public void IsNewer(string candidate, string? current, bool expected)
        => Assert.Equal(expected, VersionCompare.IsNewer(candidate, current));
}

public class GitHubUrlParseTests
{
    [Theory]
    [InlineData("https://github.com/azerothcore/mod-transmog.git", "azerothcore", "mod-transmog")]
    [InlineData("https://github.com/azerothcore/mod-transmog", "azerothcore", "mod-transmog")]
    [InlineData("git@github.com:azerothcore/mod-eluna.git", "azerothcore", "mod-eluna")]
    [InlineData("https://gitlab.com/foo/bar.git", null, null)] // not GitHub
    public void ParseGitHubUrl(string url, string? owner, string? repo)
    {
        var (o, r) = ModuleUpdateChecker.ParseGitHubUrl(url);
        Assert.Equal(owner, o);
        Assert.Equal(repo, r);
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "accontrol-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = new AppSettings
        {
            RunDirectory = @"C:\acore\bin",
            AutoStartServers = true,
        };
        settings.Schedules.Add(new ScheduledJob { Name = "Nightly", Kind = ScheduledJobKind.Restart, TimeOfDay = TimeSpan.FromHours(4) });

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(@"C:\acore\bin", loaded.RunDirectory);
        Assert.True(loaded.AutoStartServers);
        Assert.Single(loaded.Schedules);
        Assert.Equal(ScheduledJobKind.Restart, loaded.Schedules[0].Kind);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new SettingsStore(Path.Combine(_dir, "does-not-exist.json"));
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Null(loaded.RunDirectory);
    }
}
