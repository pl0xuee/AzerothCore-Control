using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

public class AcoreConfigReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "accontrol-conf-" + Guid.NewGuid().ToString("N"));

    public AcoreConfigReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Detect_ParsesDatabasesAndConnection_FromWorldserverConf()
    {
        File.WriteAllText(Path.Combine(_dir, "worldserver.conf"), """
            # AzerothCore worldserver config
            LoginDatabaseInfo     = "192.168.1.10;3307;acore;s3cret;acore_auth"
            WorldDatabaseInfo     = "192.168.1.10;3307;acore;s3cret;acore_world"
            CharacterDatabaseInfo = "192.168.1.10;3307;acore;s3cret;acore_characters"
            """);

        var result = AcoreConfigReader.Detect(_dir);

        Assert.True(result.Found);
        Assert.Equal("192.168.1.10", result.Host);
        Assert.Equal(3307, result.Port);
        Assert.Equal("acore", result.User);
        Assert.Equal("s3cret", result.Password);
        Assert.Equal(3, result.Databases.Count);
        Assert.Contains("acore_auth", result.Databases);
        Assert.Contains("acore_world", result.Databases);
        Assert.Contains("acore_characters", result.Databases);
    }

    [Fact]
    public void Detect_IgnoresDistTemplate_OnlyReadsRealConf()
    {
        // .conf.dist holds placeholder values and must NOT be used for detection.
        File.WriteAllText(Path.Combine(_dir, "authserver.conf.dist"), """
            LoginDatabaseInfo = "127.0.0.1;3306;root;placeholder;acore_auth"
            """);

        var result = AcoreConfigReader.Detect(_dir);

        Assert.False(result.Found);
    }

    [Fact]
    public void Detect_IncludesModuleDatabases_SuchAsPlayerbots()
    {
        // Modules add their own *DatabaseInfo keys; playerbots contributes PlayerbotsDatabaseInfo.
        File.WriteAllText(Path.Combine(_dir, "worldserver.conf"), """
            LoginDatabaseInfo      = "127.0.0.1;3306;acore;pw;acore_wotlk_auth"
            WorldDatabaseInfo      = "127.0.0.1;3306;acore;pw;acore_wotlk_world"
            CharacterDatabaseInfo  = "127.0.0.1;3306;acore;pw;acore_wotlk_characters"
            PlayerbotsDatabaseInfo = "127.0.0.1;3306;acore;pw;acore_wotlk_playerbots"
            LoginDatabase.WorkerThreads = 1
            """);

        var result = AcoreConfigReader.Detect(_dir);

        Assert.Equal(4, result.Databases.Count);
        Assert.Contains("acore_wotlk_playerbots", result.Databases);
    }

    [Fact]
    public void Detect_FindsModuleDatabase_InModulesSubfolder()
    {
        // Real mod-playerbots layout: core DBs in etc/worldserver.conf, the module's own DB in
        // etc/modules/playerbots.conf — a file the core-config scan never opens.
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "bin")).FullName;
        var etc = Directory.CreateDirectory(Path.Combine(_dir, "etc")).FullName;
        var modules = Directory.CreateDirectory(Path.Combine(etc, "modules")).FullName;

        File.WriteAllText(Path.Combine(etc, "worldserver.conf"), """
            LoginDatabaseInfo     = "127.0.0.1;3306;acore;pw;acore_wotlk_auth"
            WorldDatabaseInfo     = "127.0.0.1;3306;acore;pw;acore_wotlk_world"
            CharacterDatabaseInfo = "127.0.0.1;3306;acore;pw;acore_wotlk_characters"
            """);
        File.WriteAllText(Path.Combine(modules, "playerbots.conf"), """
            # PlayerbotsDatabaseInfo = "127.0.0.1;3306;acore;acore;acore_playerbots"
            PlayerbotsDatabaseInfo = "127.0.0.1;3306;acore;pw;acore_wotlk_playerbots"
            AiPlayerbot.Enabled = 1
            """);
        // The .dist template must stay ignored even inside modules/.
        File.WriteAllText(Path.Combine(modules, "othermod.conf.dist"), """
            OtherDatabaseInfo = "127.0.0.1;3306;root;placeholder;should_not_appear"
            """);

        var result = AcoreConfigReader.Detect(bin);

        Assert.Equal(
            new[] { "acore_wotlk_auth", "acore_wotlk_characters", "acore_wotlk_playerbots", "acore_wotlk_world" },
            result.Databases.Order());
    }

    [Fact]
    public void Detect_FindsConf_InSiblingEtcFolder()
    {
        // AzerothCore's Windows layout: binaries in dist/bin, configs in dist/etc.
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "bin")).FullName;
        var etc = Directory.CreateDirectory(Path.Combine(_dir, "etc")).FullName;
        File.WriteAllText(Path.Combine(etc, "worldserver.conf"), """
            # LoginDatabaseInfo = "127.0.0.1;3306;acore;acore;acore_auth"
            LoginDatabaseInfo     = "127.0.0.1;3306;acore;pw;wotlk_auth"
            WorldDatabaseInfo     = "127.0.0.1;3306;acore;pw;wotlk_world"
            CharacterDatabaseInfo = "127.0.0.1;3306;acore;pw;wotlk_chars"
            """);

        var result = AcoreConfigReader.Detect(bin);

        Assert.Equal(new[] { "wotlk_auth", "wotlk_chars", "wotlk_world" }, result.Databases.Order());
    }

    [Fact]
    public void Detect_ReturnsEmpty_WhenNoConfigPresent()
        => Assert.False(AcoreConfigReader.Detect(_dir).Found);
}
