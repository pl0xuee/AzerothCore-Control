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
    public void Detect_FallsBackToDistTemplate_WhenNoConf()
    {
        File.WriteAllText(Path.Combine(_dir, "authserver.conf.dist"), """
            LoginDatabaseInfo = "127.0.0.1;3306;root;pw;acore_auth"
            """);

        var result = AcoreConfigReader.Detect(_dir);

        Assert.True(result.Found);
        Assert.Equal("127.0.0.1", result.Host);
        Assert.Single(result.Databases);
        Assert.Equal("acore_auth", result.Databases[0]);
    }

    [Fact]
    public void Detect_ReturnsEmpty_WhenNoConfigPresent()
        => Assert.False(AcoreConfigReader.Detect(_dir).Found);
}
