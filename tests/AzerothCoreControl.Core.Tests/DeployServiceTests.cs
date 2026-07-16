using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

public class DeployServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _build;
    private readonly string _run;

    public DeployServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "accontrol-deploy-" + Guid.NewGuid().ToString("N"));
        _build = Path.Combine(_root, "build");
        _run = Path.Combine(_root, "run");
        Directory.CreateDirectory(_build);
        Directory.CreateDirectory(_run);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Deploy_UpdatesBinaries_ButNeverOverwritesUserConf()
    {
        // Live run dir: user has an edited worldserver.conf and old binaries + old template.
        const string customConf = "LoginDatabaseInfo = \"127.0.0.1;3306;acore;SECRET;acore_auth\"\nRates.XP.Kill = 10\n";
        File.WriteAllText(Path.Combine(_run, "worldserver.conf"), customConf);
        File.WriteAllText(Path.Combine(_run, "worldserver.conf.dist"), "Rates.XP.Kill = 1\n");
        File.WriteAllText(Path.Combine(_run, "worldserver.exe"), "OLD-BINARY");

        // Build output: new binaries + a new template + (crucially) a stray .conf that must be ignored.
        File.WriteAllText(Path.Combine(_build, "worldserver.exe"), "NEW-BINARY");
        File.WriteAllText(Path.Combine(_build, "authserver.exe"), "NEW-AUTH");
        File.WriteAllText(Path.Combine(_build, "worldserver.conf.dist"), "Rates.XP.Kill = 1\nRates.Honor = 2\n");
        File.WriteAllText(Path.Combine(_build, "worldserver.conf"), "THIS SHOULD NEVER BE DEPLOYED");

        var result = new DeployService().Deploy(_build, _run);

        // The user's custom conf is byte-for-byte intact.
        Assert.Equal(customConf, File.ReadAllText(Path.Combine(_run, "worldserver.conf")));
        // Binaries were updated.
        Assert.Equal("NEW-BINARY", File.ReadAllText(Path.Combine(_run, "worldserver.exe")));
        Assert.Equal("NEW-AUTH", File.ReadAllText(Path.Combine(_run, "authserver.exe")));
        // Template was updated.
        Assert.Contains("Rates.Honor", File.ReadAllText(Path.Combine(_run, "worldserver.conf.dist")));

        Assert.Contains("worldserver.exe", result.UpdatedBinaries);
        Assert.Contains("worldserver.conf.dist", result.UpdatedConfigTemplates);
        Assert.Contains("worldserver.conf", result.PreservedConfigs);
        // Old binary was backed up for rollback.
        Assert.True(File.Exists(Path.Combine(_run, "worldserver.exe.bak")));
    }

    [Fact]
    public void Deploy_ReportsNewConfigKeys_PresentInTemplateButMissingFromLiveConf()
    {
        File.WriteAllText(Path.Combine(_run, "worldserver.conf"), "Rates.XP.Kill = 10\n");
        File.WriteAllText(Path.Combine(_run, "worldserver.conf.dist"), "Rates.XP.Kill = 1\nRates.Honor = 1\nNew.Feature.Flag = 0\n");
        File.WriteAllText(Path.Combine(_build, "worldserver.exe"), "NEW");

        var result = new DeployService().Deploy(_build, _run);

        var keys = result.NewConfigKeys.Select(k => k.Key).ToList();
        Assert.Contains("Rates.Honor", keys);
        Assert.Contains("New.Feature.Flag", keys);
        Assert.DoesNotContain("Rates.XP.Kill", keys); // already present in the live conf
    }

    [Fact]
    public void Deploy_DryRun_WritesNothing()
    {
        File.WriteAllText(Path.Combine(_run, "worldserver.exe"), "OLD");
        File.WriteAllText(Path.Combine(_build, "worldserver.exe"), "NEW");

        var result = new DeployService().Deploy(_build, _run, dryRun: true);

        Assert.Equal("OLD", File.ReadAllText(Path.Combine(_run, "worldserver.exe")));
        Assert.Contains("worldserver.exe", result.UpdatedBinaries); // reported, but not applied
    }

    [Theory]
    [InlineData("worldserver.conf", true)]
    [InlineData("authserver.conf", true)]
    [InlineData("worldserver.conf.dist", false)]
    [InlineData("worldserver.exe", false)]
    public void IsUserConfig_MatchesOnlyPlainConf(string name, bool expected)
        => Assert.Equal(expected, DeployService.IsUserConfig(name));
}
