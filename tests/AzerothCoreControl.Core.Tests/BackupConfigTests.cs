using System.IO.Compression;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

/// <summary>
/// The backup's config half. No databases are configured, so mysqldump is never invoked and these run offline
/// — what's asserted is what lands in the archive.
/// </summary>
public class BackupConfigTests : IDisposable
{
    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly BackupService _backup;

    public BackupConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acc-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));

        _settings = new AppSettings { RunDirectory = Path.Combine(_root, "bin") };
        _settings.MySql.Databases = new List<string>();          // nothing to dump
        _settings.Backup.OutputDirectory = Path.Combine(_root, "out");

        _backup = new BackupService(() => _settings, new FakeTimeProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteConf(string relative, string content = "x = 1")
    {
        var path = Path.Combine(_root, "etc", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static List<string> EntriesOf(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        return zip.Entries.Select(e => e.FullName.Replace('\\', '/')).OrderBy(x => x).ToList();
    }

    [Fact]
    public async Task IncludesTheWholeConfigFolder()
    {
        // "The config folder in the run directory" — everything in it, not just the files we recognise.
        WriteConf("worldserver.conf");
        WriteConf("authserver.conf");
        WriteConf(Path.Combine("modules", "playerbots.conf"));
        WriteConf("worldserver.conf.dist");           // templates are part of the folder too
        WriteConf("something-else.txt");

        var result = await _backup.BackupAsync();

        Assert.True(result.Success, result.Message);
        var entries = EntriesOf(result.ArchivePath!);
        Assert.Contains("config/worldserver.conf", entries);
        Assert.Contains("config/authserver.conf", entries);
        Assert.Contains("config/modules/playerbots.conf", entries);
        Assert.Contains("config/worldserver.conf.dist", entries);
        Assert.Contains("config/something-else.txt", entries);
    }

    [Fact]
    public async Task ConfigContentsSurviveTheRoundTrip()
    {
        WriteConf("worldserver.conf", "GameType = 1\r\nRealmZone = 2\r\n");

        var result = await _backup.BackupAsync();

        using var zip = ZipFile.OpenRead(result.ArchivePath!);
        using var reader = new StreamReader(zip.GetEntry("config/worldserver.conf")!.Open());
        Assert.Equal("GameType = 1\r\nRealmZone = 2\r\n", reader.ReadToEnd());
    }

    [Fact]
    public async Task CanBeTurnedOff()
    {
        WriteConf("worldserver.conf");
        _settings.Backup.IncludeConfigs = false;

        var result = await _backup.BackupAsync();

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(EntriesOf(result.ArchivePath!), e => e.StartsWith("config/"));
    }

    [Fact]
    public async Task ConfigsLooseInTheRunDirectory_DoNotDragInTheWholeInstall()
    {
        // Some layouts keep .conf beside the binaries. There's no config FOLDER to copy then, and copying the
        // run directory would sweep up worldserver.exe, maps, dbc — gigabytes into every backup.
        var runDir = Path.Combine(_root, "bin");
        File.WriteAllText(Path.Combine(runDir, "worldserver.conf"), "x = 1");
        File.WriteAllText(Path.Combine(runDir, "worldserver.exe"), "MZ-not-really");

        var result = await _backup.BackupAsync();

        Assert.True(result.Success, result.Message);
        var entries = EntriesOf(result.ArchivePath!);
        Assert.Contains("config/worldserver.conf", entries);
        Assert.DoesNotContain(entries, e => e.EndsWith("worldserver.exe"));
    }

    [Fact]
    public async Task NoConfigsFound_StillProducesABackup()
    {
        var result = await _backup.BackupAsync();
        Assert.True(result.Success, result.Message);
    }
}
