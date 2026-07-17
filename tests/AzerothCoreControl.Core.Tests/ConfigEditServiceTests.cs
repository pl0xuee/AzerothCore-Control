using System.Text;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

public class ConfigEditServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly ConfigEditService _svc;

    public ConfigEditServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acc-conf-" + Guid.NewGuid().ToString("N"));
        // The AzerothCore Windows layout: binaries in bin/, configs in a sibling etc/.
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "etc", "modules"));
        _settings = new AppSettings { RunDirectory = Path.Combine(_root, "bin") };
        _svc = new ConfigEditService(() => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteConf(string relative, string content)
    {
        var path = Path.Combine(_root, "etc", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ListsCoreAndModuleConfigs_CoreFirst()
    {
        WriteConf("worldserver.conf", "x = 1");
        WriteConf("authserver.conf", "x = 1");
        WriteConf(Path.Combine("modules", "playerbots.conf"), "x = 1");

        var files = _svc.ListFiles();

        Assert.Equal(new[] { "worldserver.conf", "authserver.conf", "modules/playerbots.conf" },
            files.Select(f => f.DisplayName));
    }

    [Fact]
    public void DoesNotOfferDistTemplatesForEditing()
    {
        // Editing a .dist changes nothing the server reads — it would be a silent no-op for the user.
        WriteConf("worldserver.conf", "x = 1");
        WriteConf("worldserver.conf.dist", "x = 1");

        Assert.Single(_svc.ListFiles());
    }

    [Fact]
    public void SaveKeepsABackupOfThePreviousContents()
    {
        var path = WriteConf("worldserver.conf", "GameType = 1");

        var result = _svc.Save(path, "GameType = 0", timestamp: "stamp");

        Assert.True(result.Success, result.Message);
        Assert.Equal("GameType = 0", File.ReadAllText(path));
        Assert.Equal("GameType = 1", File.ReadAllText($"{path}.stamp.bak"));
    }

    [Fact]
    public void SaveWritesNoByteOrderMark()
    {
        // A BOM ahead of the first setting can make AzerothCore's parser choke on it.
        var path = WriteConf("worldserver.conf", "GameType = 1");

        _svc.Save(path, "GameType = 0", timestamp: "stamp");

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("GameType = 0", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void SavePreservesContentVerbatim()
    {
        // Comments, blank lines and CRLF are all meaningful to a human reading this later — don't reformat.
        var path = WriteConf("worldserver.conf", "old");
        var text = "# comment\r\n\r\nGameType = 1\r\nRealmZone = 2\r\n";

        _svc.Save(path, text, timestamp: "stamp");

        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void SaveRefusesToCreateAMissingFile()
    {
        var path = Path.Combine(_root, "etc", "not-there.conf");
        var result = _svc.Save(path, "x = 1", timestamp: "stamp");

        Assert.False(result.Success);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void LoadReturnsTheFileText()
    {
        var path = WriteConf("worldserver.conf", "GameType = 1");
        var (ok, text, _) = _svc.Load(path);

        Assert.True(ok);
        Assert.Equal("GameType = 1", text);
    }

    [Fact]
    public void LoadOfAMissingFileFailsWithAMessage()
    {
        var (ok, _, message) = _svc.Load(Path.Combine(_root, "etc", "gone.conf"));

        Assert.False(ok);
        Assert.NotEmpty(message);
    }

    [Fact]
    public void NoRunDirectory_ListsNothing()
    {
        _settings.RunDirectory = null;
        _settings.DeployDirectory = null;

        Assert.Empty(_svc.ListFiles());
    }
}
