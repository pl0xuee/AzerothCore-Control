using System.IO.Compression;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public sealed record BackupResult(bool Success, string? ArchivePath, string Message);

/// <summary>
/// Dumps the AzerothCore databases with <c>mysqldump.exe</c> and zips them into a timestamped archive,
/// pruning old archives past the retention count.
/// </summary>
public sealed class BackupService
{
    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public BackupService(Func<AppSettings> settings, TimeProvider? time = null, ILogger<BackupService>? logger = null)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger<BackupService>.Instance;
    }

    public async Task<BackupResult> BackupAsync(Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var s = _settings();
        var outDir = s.Backup.OutputDirectory;
        if (string.IsNullOrWhiteSpace(outDir))
            return new BackupResult(false, null, "Backup output directory is not configured.");
        Directory.CreateDirectory(outDir);

        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss");
        var workDir = Path.Combine(Path.GetTempPath(), $"accontrol-backup-{stamp}");
        Directory.CreateDirectory(workDir);

        try
        {
            foreach (var db in s.MySql.Databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onProgress?.Invoke($"Dumping {db}...");
                var sqlFile = Path.Combine(workDir, $"{db}.sql");
                var result = await DumpDatabaseAsync(s, db, sqlFile, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                    return new BackupResult(false, null, $"mysqldump failed for {db}: {result.StandardError}");
            }

            if (s.Backup.IncludeConfigs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copied = CopyConfigs(s, workDir, onProgress);
                onProgress?.Invoke(copied == 0
                    ? "No .conf files found to include."
                    : $"Included {copied} .conf file(s).");
            }

            var archivePath = Path.Combine(outDir, $"acore-backup-{stamp}.zip");
            onProgress?.Invoke("Compressing archive...");
            ZipFile.CreateFromDirectory(workDir, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);

            PruneOldBackups(outDir, s.Backup.RetentionCount);
            _log.LogInformation("Backup written to {Path}", archivePath);
            return new BackupResult(true, archivePath, $"Backup complete: {Path.GetFileName(archivePath)}");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Put the server's configuration into <c>config/</c> inside the archive.
    /// </summary>
    /// <remarks>
    /// Preferred path: copy the whole config folder from the run directory (including <c>modules/</c> and the
    /// .conf.dist templates) — that's what an admin means by "the config", and picking only the files we
    /// recognise would quietly drop anything else living there. When the .conf files sit loose beside the
    /// binaries there IS no such folder, and copying the run directory would drag in the entire install, so
    /// that case falls back to copying the individual .conf files. Best-effort per file: an unreadable .conf
    /// must not fail a backup whose databases dumped cleanly.
    /// </remarks>
    private int CopyConfigs(AppSettings s, string workDir, Action<string>? onProgress)
    {
        var runDir = s.RunDirectory ?? s.DeployDirectory;
        var configRoot = Path.Combine(workDir, "config");

        var configDir = AcoreConfigReader.FindConfigDirectory(runDir);
        if (configDir != null)
        {
            onProgress?.Invoke($"Including config folder {configDir}...");
            return CopyTree(configDir, configRoot, onProgress);
        }

        var copied = 0;
        foreach (var file in AcoreConfigReader.FindConfigFiles(runDir))
            copied += CopyOne(file, configRoot, onProgress);
        return copied;
    }

    /// <summary>Recursively copy a directory, skipping (and reporting) anything unreadable.</summary>
    private int CopyTree(string sourceDir, string targetDir, Action<string>? onProgress)
    {
        var copied = 0;
        try
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir))
                copied += CopyOne(file, targetDir, onProgress);

            foreach (var sub in Directory.EnumerateDirectories(sourceDir))
                copied += CopyTree(sub, Path.Combine(targetDir, Path.GetFileName(sub)), onProgress);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read config folder {Dir}", sourceDir);
            onProgress?.Invoke($"Skipped {sourceDir}: {ex.Message}");
        }
        return copied;
    }

    private int CopyOne(string file, string targetDir, Action<string>? onProgress)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not include {File} in the backup", file);
            onProgress?.Invoke($"Skipped {Path.GetFileName(file)}: {ex.Message}");
            return 0;
        }
    }

    private static Task<CommandResult> DumpDatabaseAsync(AppSettings s, string db, string outFile, CancellationToken ct)
    {
        // mysqldump writes to stdout; redirect it into the sql file via a result-file argument.
        // We use --result-file to avoid shell redirection.
        var pwd = s.MySql.Password;
        var args =
            $"--host={s.MySql.Host} --port={s.MySql.Port} --user={s.MySql.Username} " +
            (string.IsNullOrEmpty(pwd) ? "" : $"--password={pwd} ") +
            $"--single-transaction --routines --triggers --result-file=\"{outFile}\" {db}";
        return CommandRunner.RunAsync(s.Backup.MysqlDumpPath, args, cancellationToken: ct);
    }

    private void PruneOldBackups(string dir, int retention)
    {
        if (retention <= 0) return;
        var archives = new DirectoryInfo(dir)
            .GetFiles("acore-backup-*.zip")
            .OrderByDescending(f => f.Name)
            .Skip(retention)
            .ToList();
        foreach (var old in archives)
        {
            try { old.Delete(); _log.LogInformation("Pruned old backup {Name}", old.Name); }
            catch (IOException) { /* in use — skip */ }
        }
    }
}
