using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

public class ModuleUpdateCheckerTests : IDisposable
{
    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly ModuleUpdateChecker _checker;

    public ModuleUpdateCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acc-modules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "modules"));
        _settings = new AppSettings { SourceDirectory = _root };
        _checker = new ModuleUpdateChecker(() => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string AddModule(string name, bool asGitRepo)
    {
        var path = Path.Combine(_root, "modules", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "CMakeLists.txt"), "# module");
        if (asGitRepo)
            LibGit2Sharp.Repository.Init(path);
        return path;
    }

    [Fact]
    public void ListsModulesInstalledFromAZip_NotJustGitClones()
    {
        // Regression: the list filtered on Repository.IsValid, so a module installed by unzipping a GitHub
        // download simply never appeared in the Modules tab — with nothing to say why.
        AddModule("mod-playerbots", asGitRepo: true);
        AddModule("mod-from-zip", asGitRepo: false);

        var paths = _checker.ListModulePaths().Select(Path.GetFileName).ToList();

        Assert.Equal(new[] { "mod-from-zip", "mod-playerbots" }, paths);
    }

    [Fact]
    public async Task ANonGitModule_ExplainsItselfInsteadOfVanishing()
    {
        var path = AddModule("mod-from-zip", asGitRepo: false);

        var status = await _checker.CheckOneAsync(path);

        Assert.Equal("mod-from-zip", status.Name);
        Assert.NotNull(status.Error);
        Assert.Contains("not a git repository", status.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(status.UpdateAvailable);
        Assert.False(status.CanFastForward); // nothing to pull — the button must stay disabled
    }

    [Fact]
    public void NoModulesFolder_ListsNothingRatherThanThrowing()
    {
        _settings.SourceDirectory = Path.Combine(_root, "does-not-exist");

        Assert.Null(_checker.ModulesFolder);
        Assert.Empty(_checker.ListModulePaths());
    }

    [Fact]
    public async Task AnOverriddenModule_ReportsTheForkAndOffersToCloneIt()
    {
        // End-to-end: a ZIP-installed fork must be identified as the fork, not as the catalogue's popular
        // upstream — otherwise Re-clone would replace the user's fixed version with the one they left.
        // No network: the override short-circuits the catalogue lookup entirely.
        var path = AddModule("mod-challenge-modes", asGitRepo: false);
        _settings.ModuleRepoOverrides.Add(new ModuleRepoOverride
        {
            Module = "mod-challenge-modes",
            Repository = "poemihai/mod-challenge-modes",
        });

        var status = await _checker.CheckOneAsync(path);

        Assert.Equal("poemihai/mod-challenge-modes", status.GitHubRepo);
        Assert.Equal("https://github.com/poemihai/mod-challenge-modes.git", status.CloneUrl);
        Assert.True(status.IdentifiedFromCatalogue);
        Assert.False(status.IsGitRepo);
    }

    [Fact]
    public void FindsModulesUnderTheSourceDirectory()
    {
        Assert.Equal(Path.Combine(_root, "modules"), _checker.FindModulesFolder().Path);
    }

    [Fact]
    public void SourceDirectoryPointedAtModulesItself_StillWorks()
    {
        // An easy way to set it wrong, and there's no reason to punish it.
        _settings.SourceDirectory = Path.Combine(_root, "modules");
        AddModule("mod-demo", asGitRepo: true);

        Assert.Equal(Path.Combine(_root, "modules"), _checker.FindModulesFolder().Path);
    }

    [Fact]
    public void SourceDirectoryPointedAboveTheSourceTree_StillWorks()
    {
        // e.g. C:\AzerothCore instead of C:\AzerothCore\azerothcore-wotlk.
        var parent = Path.Combine(_root, "..", "acc-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var nested = Path.Combine(parent, "azerothcore-wotlk");
            Directory.CreateDirectory(Path.Combine(nested, "modules"));
            _settings.SourceDirectory = parent;

            Assert.Equal(Path.Combine(nested, "modules"), _checker.FindModulesFolder().Path);
        }
        finally { try { Directory.Delete(parent, recursive: true); } catch { } }
    }

    [Fact]
    public void WithNoSourceDirectory_RecoversModulesFromTheRunDirectory()
    {
        // The real-world case: Source directory blank but Run directory set. AzerothCore's Windows layout is
        // <source>/env/dist/bin/worldserver.exe, so modules/ is an ancestor of the run directory — no reason
        // to make the user type a path the app can find itself.
        _settings.SourceDirectory = null;
        var runDir = Path.Combine(_root, "env", "dist", "bin");
        Directory.CreateDirectory(runDir);
        _settings.RunDirectory = runDir;
        AddModule("mod-demo", asGitRepo: true);

        var result = _checker.FindModulesFolder();

        Assert.Equal(Path.Combine(_root, "modules"), result.Path);
        Assert.Single(_checker.ListModulePaths());
    }

    [Fact]
    public void WhenTheSourceDirectoryIsUnset_AndNothingCanBeRecovered_TheMessageSaysSo()
    {
        _settings.SourceDirectory = null;
        _settings.RunDirectory = null;
        _settings.DeployDirectory = null;

        var result = _checker.FindModulesFolder();

        Assert.Null(result.Path);
        Assert.Contains("Source directory isn't set", result.Detail);
    }

    [Fact]
    public void WhenNoModulesFolderExists_TheMessageNamesThePathItChecked()
    {
        // "No modules folder found" with no path is unactionable — the user can't tell a wrong setting from
        // a broken app.
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        _settings.SourceDirectory = empty;

        var result = _checker.FindModulesFolder();

        Assert.Null(result.Path);
        Assert.Contains(empty, result.Detail);
    }
}
