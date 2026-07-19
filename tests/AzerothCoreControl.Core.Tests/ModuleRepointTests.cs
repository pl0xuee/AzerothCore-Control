using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using LibGit2Sharp;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

/// <summary>
/// Repointing rewrites where a module looks for updates, so these tests are about it never quietly losing
/// work: no merge, no reset, and a failed fetch leaving the original remote intact. All "remotes" are local
/// repos, so no network is involved.
/// </summary>
public class ModuleRepointTests : IDisposable
{
    private readonly string _root;
    private readonly ModuleUpdater _updater;

    public ModuleRepointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acc-repoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _updater = new ModuleUpdater(() => new AppSettings());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly Signature Sig = new("t", "t@t", DateTimeOffset.FromUnixTimeSeconds(1700000000));

    /// <summary>A repo with one commit, usable as a clone source.</summary>
    private string CreateRepo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Repository.Init(path);
        Commit(path, "README.md", "initial");
        return path;
    }

    /// <summary>Add a commit to a repo, so histories can be made to move on independently.</summary>
    private static string Commit(string path, string file, string content)
    {
        File.WriteAllText(Path.Combine(path, file), content);
        using var repo = new Repository(path);
        Commands.Stage(repo, "*");
        return repo.Commit($"add {file}", Sig, Sig, new CommitOptions()).Sha;
    }

    private static string OriginUrl(string modulePath)
    {
        using var repo = new Repository(modulePath);
        return repo.Network.Remotes["origin"].Url;
    }

    [Fact]
    public void PointingAtAForkThatIsAhead_OffersAFastForward()
    {
        // The happy case: the fork is upstream plus extra commits, so an ordinary pull can get there.
        var upstream = CreateRepo("upstream");
        var module = Path.Combine(_root, "mod-thing");
        Repository.Clone(upstream, module);

        var fork = Path.Combine(_root, "fork");
        Repository.Clone(upstream, fork);
        Commit(fork, "fix.cpp", "the fix");

        var result = _updater.RepointRemote(module, fork);

        Assert.True(result.Success);
        Assert.True(result.CanFastForward);
        Assert.False(result.Diverged);
        Assert.Equal(fork, OriginUrl(module));
    }

    [Fact]
    public void PointingAtADivergedFork_SaysSoRatherThanResetting()
    {
        // The realistic case: a fork with its own commits, while the local checkout has moved on too. No pull
        // can cross that, and the module must be left exactly as it was rather than reset onto the fork.
        var upstream = CreateRepo("upstream");
        var module = Path.Combine(_root, "mod-thing");
        Repository.Clone(upstream, module);

        var fork = Path.Combine(_root, "fork");
        Repository.Clone(upstream, fork);
        Commit(fork, "fork-only.cpp", "fork work");

        var localSha = Commit(module, "local-only.cpp", "local work");

        var result = _updater.RepointRemote(module, fork);

        Assert.True(result.Success);
        Assert.True(result.Diverged);
        Assert.False(result.CanFastForward);
        Assert.Contains("re-clone", result.Message, StringComparison.OrdinalIgnoreCase);

        // The local commit is untouched — nothing was merged or reset.
        using var repo = new Repository(module);
        Assert.Equal(localSha, repo.Head.Tip.Sha);
    }

    [Fact]
    public void AnUnreachableRemote_LeavesTheOriginalInPlace()
    {
        // Half-applying the switch would leave the module unable to update from anywhere.
        var upstream = CreateRepo("upstream");
        var module = Path.Combine(_root, "mod-thing");
        Repository.Clone(upstream, module);

        var result = _updater.RepointRemote(module, Path.Combine(_root, "does-not-exist"));

        Assert.False(result.Success);
        Assert.Contains("unchanged", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(upstream, OriginUrl(module));
    }

    [Fact]
    public void ADirtyTree_IsRefusedBeforeAnythingChanges()
    {
        var upstream = CreateRepo("upstream");
        var module = Path.Combine(_root, "mod-thing");
        Repository.Clone(upstream, module);
        File.WriteAllText(Path.Combine(module, "README.md"), "the user's local edit");

        var fork = CreateRepo("fork");
        var result = _updater.RepointRemote(module, fork);

        Assert.False(result.Success);
        Assert.Equal(upstream, OriginUrl(module));
        // And the edit is still there.
        Assert.Equal("the user's local edit", File.ReadAllText(Path.Combine(module, "README.md")));
    }

    [Fact]
    public void ANonGitFolder_IsSentToReclone()
    {
        var folder = Path.Combine(_root, "mod-zipped");
        Directory.CreateDirectory(folder);

        var result = _updater.RepointRemote(folder, CreateRepo("fork"));

        Assert.False(result.Success);
        Assert.Contains("Re-clone", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecloningOverAGitRepo_NeedsToBeAskedFor()
    {
        // The guard exists so a plain "Re-clone" can't blow away a working checkout; moving onto a diverged
        // fork is the one case that legitimately needs it, and it has to say so explicitly.
        var module = CreateRepo("mod-thing");
        var fork = CreateRepo("fork");

        Assert.False(_updater.Reclone(module, fork).Success);
        Assert.True(_updater.Reclone(module, fork, replaceGitRepo: true).Success);
    }
}

/// <summary>
/// Whether a pin counts as disagreeing with the checkout's actual origin. This is what decides if the app
/// offers to switch remotes at all, so a false positive nags about a module that's already correct.
/// </summary>
public class RemoteMismatchTests
{
    private static List<ModuleRepoOverride> Pin(string module, string repo) =>
        new() { new ModuleRepoOverride { Module = module, Repository = repo } };

    [Fact]
    public void APinNamingADifferentRepo_IsAMismatch()
    {
        var result = ModuleUpdateChecker.FindRemoteMismatch(
            Pin("mod-challenge-modes", "poemihai/mod-challenge-modes"),
            "mod-challenge-modes", "AldebaraanMKII", "mod-challenge-modes");

        Assert.NotNull(result);
        Assert.Equal("poemihai/mod-challenge-modes", result!.FullName);
    }

    [Fact]
    public void APinTheCheckoutAlreadyFollows_IsNotAMismatch()
    {
        // Pinning a module you're already on is how you record "yes, this fork is deliberate" — it must not
        // then pester you to switch to where you already are.
        var result = ModuleUpdateChecker.FindRemoteMismatch(
            Pin("mod-challenge-modes", "poemihai/mod-challenge-modes"),
            "mod-challenge-modes", "poemihai", "mod-challenge-modes");

        Assert.Null(result);
    }

    [Fact]
    public void CaseDoesNotMakeAMismatch()
    {
        // GitHub owners are case-insensitive, and the casing in a hand-typed pin rarely matches the remote.
        var result = ModuleUpdateChecker.FindRemoteMismatch(
            Pin("mod-challenge-modes", "POEMIHAI/Mod-Challenge-Modes"),
            "mod-challenge-modes", "poemihai", "mod-challenge-modes");

        Assert.Null(result);
    }

    [Fact]
    public void NoPin_IsNeverAMismatch()
    {
        Assert.Null(ModuleUpdateChecker.FindRemoteMismatch(
            new List<ModuleRepoOverride>(), "mod-transmog", "azerothcore", "mod-transmog"));
        Assert.Null(ModuleUpdateChecker.FindRemoteMismatch(
            null, "mod-transmog", "azerothcore", "mod-transmog"));
    }

    [Fact]
    public void APinnedModuleWithNoParseableRemote_IsActionable()
    {
        // A checkout whose origin isn't GitHub (or has none at all) can't be update-checked, and the pin says
        // exactly where it should point — that's worth offering rather than staying silent.
        var result = ModuleUpdateChecker.FindRemoteMismatch(
            Pin("mod-challenge-modes", "poemihai/mod-challenge-modes"),
            "mod-challenge-modes", null, null);

        Assert.NotNull(result);
    }
}

/// <summary>
/// Force-replace exists for a module stuck on old code that a pull cannot reach. It is destructive by design,
/// so these tests are about it staying recoverable and never running uninvited.
/// </summary>
public class ModuleForceReplaceTests : IDisposable
{
    private readonly string _root;
    private readonly ModuleUpdater _updater;

    private string ModulesDir => Path.Combine(_root, "modules");

    public ModuleForceReplaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acc-force-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ModulesDir);
        _updater = new ModuleUpdater(() => new AppSettings());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly Signature Sig = new("t", "t@t", DateTimeOffset.FromUnixTimeSeconds(1700000000));

    private string CreateUpstream(string content)
    {
        var path = Path.Combine(_root, "upstream");
        Directory.CreateDirectory(path);
        Repository.Init(path);
        File.WriteAllText(Path.Combine(path, "ChallengeModes.cpp"), content);
        using var repo = new Repository(path);
        Commands.Stage(repo, "*");
        repo.Commit("init", Sig, Sig, new CommitOptions());
        return path;
    }

    [Fact]
    public void ADirtyModule_IsReplacedWithTheRemotesLatest()
    {
        // The case that motivated this: a module a pull refuses, stuck on code that no longer compiles, and
        // therefore blocking every other module in the shared build target.
        var upstream = CreateUpstream("the fixed code");
        var module = Path.Combine(ModulesDir, "mod-challenge-modes");
        Repository.Clone(upstream, module);
        File.WriteAllText(Path.Combine(module, "ChallengeModes.cpp"), "the old broken code");

        Assert.False(_updater.Pull(module).Success);   // a pull cannot get there

        var result = _updater.ForceReplace(module);

        Assert.True(result.Success);
        Assert.Equal("the fixed code", File.ReadAllText(Path.Combine(module, "ChallengeModes.cpp")));
    }

    [Fact]
    public void TheReplacedFolder_IsKeptAsABackup()
    {
        // Destructive is acceptable only because it is recoverable — the user's edits must still exist
        // somewhere afterwards.
        var upstream = CreateUpstream("the fixed code");
        var module = Path.Combine(ModulesDir, "mod-challenge-modes");
        Repository.Clone(upstream, module);
        File.WriteAllText(Path.Combine(module, "my-notes.txt"), "hours of local work");

        var result = _updater.ForceReplace(module);

        Assert.True(result.Success);
        Assert.NotNull(result.BackupPath);
        Assert.Equal("hours of local work", File.ReadAllText(Path.Combine(result.BackupPath!, "my-notes.txt")));
        // And the backup lives outside modules/, or CMake would try to build it as a second copy.
        Assert.DoesNotContain(ModulesDir, result.BackupPath!, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleWithNoRemote_IsRefusedRatherThanEmptied()
    {
        // Nothing to re-clone from. Moving the folder aside anyway would destroy it for no gain.
        var module = Path.Combine(ModulesDir, "mod-local-only");
        Directory.CreateDirectory(module);
        Repository.Init(module);
        File.WriteAllText(Path.Combine(module, "keep.txt"), "irreplaceable");

        var result = _updater.ForceReplace(module);

        Assert.False(result.Success);
        Assert.Contains("no remote", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("irreplaceable", File.ReadAllText(Path.Combine(module, "keep.txt")));
    }

    [Fact]
    public void ANonGitFolder_IsRefused()
    {
        var folder = Path.Combine(ModulesDir, "mod-zipped");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "irreplaceable");

        var result = _updater.ForceReplace(folder);

        Assert.False(result.Success);
        Assert.Equal("irreplaceable", File.ReadAllText(Path.Combine(folder, "keep.txt")));
    }
}
