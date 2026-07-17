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
}
