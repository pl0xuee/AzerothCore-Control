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

public class ModuleCatalogueResolveTests
{
    private static CatalogueEntry Entry(string name, string fullName, int stars = 0, bool archived = false)
        => new(name, fullName, $"https://github.com/{fullName}.git", null, stars, archived);

    [Fact]
    public void MatchesAFolderNameToItsRepo()
    {
        var all = new[] { Entry("mod-transmog", "azerothcore/mod-transmog"), Entry("mod-ah-bot", "azerothcore/mod-ah-bot") };
        Assert.Equal("azerothcore/mod-transmog", ModuleCatalogue.Resolve(all, "mod-transmog")?.FullName);
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var all = new[] { Entry("mod-transmog", "azerothcore/mod-transmog") };
        Assert.NotNull(ModuleCatalogue.Resolve(all, "Mod-Transmog"));
    }

    [Fact]
    public void PrefersTheMostStarredWhenForksShareTheName()
    {
        // Forks carry the catalogue topic too, so a name legitimately matches several repos. The canonical
        // upstream is the popular one — cloning someone's stale fork would be worse than doing nothing.
        var all = new[]
        {
            Entry("mod-transmog", "randomuser/mod-transmog", stars: 3),
            Entry("mod-transmog", "azerothcore/mod-transmog", stars: 400),
        };
        Assert.Equal("azerothcore/mod-transmog", ModuleCatalogue.Resolve(all, "mod-transmog")?.FullName);
    }

    [Fact]
    public void PrefersAMaintainedRepoOverAnArchivedOne()
    {
        var all = new[]
        {
            Entry("mod-transmog", "old/mod-transmog", stars: 900, archived: true),
            Entry("mod-transmog", "azerothcore/mod-transmog", stars: 400),
        };
        Assert.Equal("azerothcore/mod-transmog", ModuleCatalogue.Resolve(all, "mod-transmog")?.FullName);
    }

    [Fact]
    public void NoMatchIsNull()
    {
        var all = new[] { Entry("mod-transmog", "azerothcore/mod-transmog") };
        Assert.Null(ModuleCatalogue.Resolve(all, "mod-something-homemade"));
        Assert.Null(ModuleCatalogue.Resolve(all, ""));
    }

    [Fact]
    public void DoesNotMatchOnAPartialName()
    {
        // "mod-playerbots" must not resolve to "mod-playerbots-characters".
        var all = new[] { Entry("mod-playerbots-characters", "deseven/mod-playerbots-characters") };
        Assert.Null(ModuleCatalogue.Resolve(all, "mod-playerbots"));
    }
}

public class ModuleRepoOverrideTests
{
    private static List<ModuleRepoOverride> Overrides(string module, string repo)
        => new() { new ModuleRepoOverride { Module = module, Repository = repo } };

    [Fact]
    public void PinsAModuleToAFork_InsteadOfTheCataloguesGuess()
    {
        // The real case: the catalogue resolves mod-challenge-modes to the 65-star ZhengPeiRu21 upstream,
        // but this server runs poemihai's fixed fork — which isn't in the catalogue at all.
        var entry = ModuleCatalogue.FindOverride(
            Overrides("mod-challenge-modes", "poemihai/mod-challenge-modes"), "mod-challenge-modes");

        Assert.NotNull(entry);
        Assert.Equal("poemihai/mod-challenge-modes", entry!.FullName);
        Assert.Equal("https://github.com/poemihai/mod-challenge-modes.git", entry.CloneUrl);
    }

    [Fact]
    public void MatchingTheModuleFolderIsCaseInsensitiveAndTrimmed()
    {
        var entry = ModuleCatalogue.FindOverride(
            Overrides("  Mod-Challenge-Modes  ", "poemihai/mod-challenge-modes"), "mod-challenge-modes");
        Assert.NotNull(entry);
    }

    [Fact]
    public void AnUnrelatedModuleIsUnaffected()
    {
        Assert.Null(ModuleCatalogue.FindOverride(
            Overrides("mod-challenge-modes", "poemihai/mod-challenge-modes"), "mod-transmog"));
    }

    [Theory]
    [InlineData("poemihai/mod-challenge-modes")]
    [InlineData("https://github.com/poemihai/mod-challenge-modes")]
    [InlineData("https://github.com/poemihai/mod-challenge-modes.git")]
    [InlineData("git@github.com:poemihai/mod-challenge-modes.git")]
    [InlineData("  poemihai/mod-challenge-modes  ")]
    public void AcceptsTheFormsPeopleActuallyPasteIn(string spec)
    {
        var entry = ModuleCatalogue.FromRepoSpec("mod-challenge-modes", spec);

        Assert.NotNull(entry);
        Assert.Equal("poemihai/mod-challenge-modes", entry!.FullName);
        Assert.Equal("https://github.com/poemihai/mod-challenge-modes.git", entry.CloneUrl);
    }

    [Fact]
    public void TheEntryNameIsTheRepoName_NotTheFolderName()
    {
        // A fork may be named differently to the folder; the GitHub release lookup uses this name, so a
        // folder name here would 404 against the wrong repo.
        var entry = ModuleCatalogue.FromRepoSpec("mod-foo", "someone/mod-foo-fixed");
        Assert.Equal("mod-foo-fixed", entry!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-repo")]
    [InlineData("too/many/parts")]
    [InlineData("https://example.com/whatever")]
    public void ATypoDegradesToUnknown_RatherThanABogusCloneUrl(string? spec)
    {
        // A bad override must never produce a clone URL — Re-clone would replace a module from it.
        Assert.Null(ModuleCatalogue.FromRepoSpec("mod-foo", spec));
    }
}

public class BuildDiagnosticsTests
{
    [Fact]
    public void PicksCompilerErrorsOutOfNoise()
    {
        var output = new[]
        {
            "[build] cmake --build ...",
            "  playerbot.cpp",
            @"C:\src\mod-playerbots\src\Bot.cpp(88,12): error C2065: 'foo': undeclared identifier [C:\build\mod.vcxproj]",
            "  Generating code...",
            "LINK : fatal error LNK1104: cannot open file 'ace.lib' [C:\\build\\worldserver.vcxproj]",
            "    2 Error(s)",
        };

        var errors = BuildDiagnostics.ExtractErrors(output);

        Assert.Equal(2, errors.Count);
        Assert.Contains("C2065", errors[0]);
        Assert.Contains("LNK1104", errors[1]);
    }

    [Fact]
    public void DedupesTheSameErrorReportedBySeveralProjects()
    {
        var output = new[]
        {
            @"C:\src\Bot.cpp(88,12): error C2065: 'foo': undeclared identifier [C:\build\a.vcxproj]",
            @"C:\src\Bot.cpp(88,12): error C2065: 'foo': undeclared identifier [C:\build\b.vcxproj]",
        };

        Assert.Single(BuildDiagnostics.ExtractErrors(output));
    }

    [Fact]
    public void FindsCMakeConfigureErrors()
    {
        var output = new[] { "CMake Error at CMakeLists.txt:5 (find_package):", "  Could not find MySQL." };
        Assert.Single(BuildDiagnostics.ExtractErrors(output));
    }

    [Fact]
    public void IgnoresSummaryLinesThatMentionErrors()
    {
        var output = new[] { "    0 Error(s)", "Build succeeded.", "[build] cmake --build ..." };
        Assert.Empty(BuildDiagnostics.ExtractErrors(output));
    }

    [Fact]
    public void CapsTheNumberOfErrors()
    {
        var output = Enumerable.Range(0, 100).Select(i => $"file{i}.cpp(1,1): error C2065: bad {i}");
        Assert.Equal(BuildDiagnostics.MaxErrors, BuildDiagnostics.ExtractErrors(output).Count);
    }
}

public class ResolveCMakeGuiTests
{
    [Fact]
    public void PrefersExplicitPath()
    {
        var build = new BuildSettings { CMakePath = "cmake", CMakeGuiPath = @"D:\tools\cmake-gui.exe" };
        Assert.Equal(@"D:\tools\cmake-gui.exe", BuildService.ResolveCMakeGui(build));
    }

    [Fact]
    public void FallsBackToPathLookupWhenCMakeIsBare()
    {
        var build = new BuildSettings { CMakePath = "cmake" };
        Assert.Equal("cmake-gui", BuildService.ResolveCMakeGui(build));
    }

    [Fact]
    public void FallsBackToPathLookupWhenNoGuiBesideCMake()
    {
        // A real directory with no cmake-gui in it — the sibling probe must miss and not invent a path.
        var dir = Path.Combine(Path.GetTempPath(), "acc-gui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var build = new BuildSettings { CMakePath = Path.Combine(dir, "cmake.exe") };
            Assert.Equal("cmake-gui", BuildService.ResolveCMakeGui(build));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FindsGuiBesideCMake()
    {
        var dir = Path.Combine(Path.GetTempPath(), "acc-gui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var guiName = OperatingSystem.IsWindows() ? "cmake-gui.exe" : "cmake-gui";
            var gui = Path.Combine(dir, guiName);
            File.WriteAllText(gui, "");
            var build = new BuildSettings { CMakePath = Path.Combine(dir, "cmake.exe") };
            Assert.Equal(gui, BuildService.ResolveCMakeGui(build));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
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
