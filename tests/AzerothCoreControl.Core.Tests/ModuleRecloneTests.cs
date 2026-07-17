using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

/// <summary>
/// Re-clone replaces a folder the user owns, so these tests are mostly about what must NEVER happen: losing
/// their files. The "clone" source is a local git repo, so no network is involved.
/// </summary>
public class ModuleRecloneTests : IDisposable
{
    private readonly string _root;
    private readonly ModuleUpdater _updater;

    /// <summary>Mirrors the real layout: modules live in &lt;source&gt;/modules/&lt;name&gt;.</summary>
    private string ModulesDir => Path.Combine(_root, "modules");

    public ModuleRecloneTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acc-reclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ModulesDir);
        _updater = new ModuleUpdater(() => new AppSettings());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A real git repo with one commit, used as the clone source.</summary>
    private string CreateUpstream(string name)
    {
        var path = Path.Combine(_root, name + "-upstream");
        Directory.CreateDirectory(path);
        LibGit2Sharp.Repository.Init(path);
        File.WriteAllText(Path.Combine(path, "README.md"), "upstream content");
        using var repo = new LibGit2Sharp.Repository(path);
        LibGit2Sharp.Commands.Stage(repo, "*");
        var sig = new LibGit2Sharp.Signature("t", "t@t", DateTimeOffset.Now);
        repo.Commit("init", sig, sig, new LibGit2Sharp.CommitOptions());
        return path;
    }

    private string CreateZipStyleModule(string name)
    {
        var path = Path.Combine(ModulesDir, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "my-edit.txt"), "the user's local edit");
        File.WriteAllText(Path.Combine(path, "CMakeLists.txt"), "# module build script");
        return path;
    }

    [Fact]
    public void ReclonesAZipModule_AndKeepsTheOldFolder()
    {
        var upstream = CreateUpstream("mod-demo");
        var module = CreateZipStyleModule("mod-demo");

        var result = _updater.Reclone(module, upstream, timestamp: "stamp");

        Assert.True(result.Success, result.Message);
        Assert.True(LibGit2Sharp.Repository.IsValid(module));            // now update-checkable
        Assert.True(File.Exists(Path.Combine(module, "README.md")));     // upstream content arrived

        // The user's folder is kept, not deleted — it may hold edits we can't know about.
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(Path.Combine(result.BackupPath!, "my-edit.txt")));
    }

    [Fact]
    public void TheBackupIsKeptOutsideTheModulesFolder()
    {
        // AzerothCore's modules/CMakeLists.txt adds EVERY subdirectory, so a backup left in there would
        // declare the module a second time and break the very rebuild a re-clone tells the user to run.
        // It would also show up as a bogus row in the Modules tab.
        var upstream = CreateUpstream("mod-demo");
        var module = CreateZipStyleModule("mod-demo");

        var result = _updater.Reclone(module, upstream, timestamp: "stamp");

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(result.BackupPath!, "CMakeLists.txt"))); // it IS a buildable copy
        Assert.Equal(new[] { "mod-demo" }, Directory.GetDirectories(ModulesDir).Select(Path.GetFileName));
        Assert.StartsWith(Path.Combine(_root, "module-backups"), result.BackupPath!);
    }

    [Fact]
    public void AFailedClone_RestoresTheOriginalFolder()
    {
        // The single most important behaviour: a failed re-clone must not cost the user their module.
        var module = CreateZipStyleModule("mod-demo");

        var result = _updater.Reclone(module, Path.Combine(_root, "does-not-exist"), timestamp: "stamp");

        Assert.False(result.Success);
        Assert.True(Directory.Exists(module));
        Assert.True(File.Exists(Path.Combine(module, "my-edit.txt")));
        Assert.False(Directory.Exists(ModuleUpdater.BackupPathFor(module, "stamp")!)); // moved back, nothing left
    }

    [Fact]
    public void RefusesToRecloneAnExistingGitRepo()
    {
        // That folder has history and possibly local commits — Pull is the right tool, not a replace.
        var upstream = CreateUpstream("mod-demo");
        var module = Path.Combine(ModulesDir, "mod-demo");
        Directory.CreateDirectory(module);
        LibGit2Sharp.Repository.Init(module);

        var result = _updater.Reclone(module, upstream, timestamp: "stamp");

        Assert.False(result.Success);
        Assert.Contains("already a git repository", result.Message);
    }

    [Fact]
    public void RefusesWhenTheBackupNameIsTaken()
    {
        // Never silently overwrite an earlier backup — that's the user's only copy of their edits.
        var upstream = CreateUpstream("mod-demo");
        var module = CreateZipStyleModule("mod-demo");
        Directory.CreateDirectory(ModuleUpdater.BackupPathFor(module, "stamp")!);

        var result = _updater.Reclone(module, upstream, timestamp: "stamp");

        Assert.False(result.Success);
        Assert.True(File.Exists(Path.Combine(module, "my-edit.txt"))); // untouched
    }
}
