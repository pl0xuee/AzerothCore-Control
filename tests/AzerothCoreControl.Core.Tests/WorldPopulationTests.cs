using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

/// <summary>
/// No MySQL here — these cover everything the query is BUILT from: which database each connection belongs to,
/// and the bot account prefix. Getting either wrong means counting the wrong thing, or nothing.
/// </summary>
public class WorldPopulationTests : IDisposable
{
    private readonly string _root;
    private readonly string _runDir;
    private readonly AppSettings _settings;
    private readonly WorldPopulationService _svc;

    public WorldPopulationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acc-pop-" + Guid.NewGuid().ToString("N"));
        _runDir = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_runDir);
        Directory.CreateDirectory(Path.Combine(_root, "etc", "modules"));
        _settings = new AppSettings { RunDirectory = _runDir };
        _svc = new WorldPopulationService(() => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteConf(string relative, string body)
        => File.WriteAllText(Path.Combine(_root, "etc", relative), body);

    [Fact]
    public void ReadsTheBotAccountPrefixFromPlayerbotsConf()
    {
        // mod-playerbots identifies its own bots by account-name prefix, so this IS the definition of a bot.
        WriteConf(Path.Combine("modules", "playerbots.conf"), """
            AiPlayerbot.Enabled = 1
            AiPlayerbot.RandomBotAccountPrefix = "mybots"
            """);

        Assert.Equal("mybots", _svc.ReadBotAccountPrefix(_runDir));
    }

    [Fact]
    public void FallsBackToThePlayerbotsDefaultPrefix()
    {
        // No playerbots.conf at all — "rndbot" is the module's own default.
        Assert.Equal("rndbot", _svc.ReadBotAccountPrefix(_runDir));
        Assert.Equal("rndbot", _svc.ReadBotAccountPrefix(null));
    }

    [Fact]
    public void AConfWithoutThePrefixKeyStillFallsBack()
    {
        WriteConf(Path.Combine("modules", "playerbots.conf"), "AiPlayerbot.Enabled = 1\n");
        Assert.Equal("rndbot", _svc.ReadBotAccountPrefix(_runDir));
    }

    [Fact]
    public void FindsEachDatabaseByItsOwnConfigKey()
    {
        // Accounts live in the login DB and characters in the character DB — the count joins across the two,
        // so they must be told apart. The backup path only ever needed the flat list, which cannot.
        WriteConf("worldserver.conf", """
            LoginDatabaseInfo     = "127.0.0.1;3306;acore;pw;acore_auth"
            WorldDatabaseInfo     = "127.0.0.1;3306;acore;pw;acore_world"
            CharacterDatabaseInfo = "127.0.0.1;3306;acore;pw;acore_characters"
            """);

        Assert.Equal("acore_auth", AcoreConfigReader.FindDatabaseInfo(_runDir, "LoginDatabaseInfo")?.Database);
        Assert.Equal("acore_characters", AcoreConfigReader.FindDatabaseInfo(_runDir, "CharacterDatabaseInfo")?.Database);
        Assert.Equal("acore_world", AcoreConfigReader.FindDatabaseInfo(_runDir, "WorldDatabaseInfo")?.Database);
    }

    [Fact]
    public void DatabaseInfoCarriesTheWholeConnection()
    {
        WriteConf("worldserver.conf", """
            CharacterDatabaseInfo = "db.local;3307;acore;s3cret;acore_characters"
            """);

        var info = AcoreConfigReader.FindDatabaseInfo(_runDir, "CharacterDatabaseInfo");

        Assert.NotNull(info);
        Assert.Equal("db.local", info!.Host);
        Assert.Equal(3307, info.Port);
        Assert.Equal("acore", info.User);
        Assert.Equal("s3cret", info.Password);
    }

    [Fact]
    public void AMissingKeyIsNull()
    {
        WriteConf("worldserver.conf", "GameType = 1\n");
        Assert.Null(AcoreConfigReader.FindDatabaseInfo(_runDir, "CharacterDatabaseInfo"));
        Assert.Null(AcoreConfigReader.FindDatabaseInfo(null, "CharacterDatabaseInfo"));
    }

    [Fact]
    public async Task WithNoConfig_TheCountIsUnknownRatherThanAnError()
    {
        // The card shows "—". It must never throw: a dashboard tile can't be allowed to take the app down.
        Assert.Null(await _svc.QueryAsync());
    }
}
