using System.Text;
using AzerothCoreControl.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

/// <summary>A .conf file available for editing.</summary>
public sealed record ConfigFileInfo(string Path, string Name, string Folder)
{
    /// <summary>"worldserver.conf" or "modules/playerbots.conf" — enough to tell same-named files apart.</summary>
    public string DisplayName => Folder.Length == 0 ? Name : $"{Folder}/{Name}";
}

public sealed record ConfigSaveResult(bool Success, string Message, string? BackupPath = null);

/// <summary>
/// Reads and writes the server's .conf files for the in-app editor.
/// </summary>
/// <remarks>
/// These files are the difference between a server that boots and one that doesn't, and they're the one thing
/// a deploy deliberately never overwrites. So every write keeps a timestamped .bak of what was there before,
/// and nothing here interprets the contents — the file is passed through verbatim, because AzerothCore's
/// parser, not this app, is the authority on what's valid.
/// </remarks>
public sealed class ConfigEditService
{
    /// <summary>Refuse to load anything absurd for a text box — a .conf is a few hundred KB at most.</summary>
    private const long MaxSizeBytes = 8 * 1024 * 1024;

    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;

    public ConfigEditService(Func<AppSettings> settings, ILogger<ConfigEditService>? logger = null)
    {
        _settings = settings;
        _log = logger ?? NullLogger<ConfigEditService>.Instance;
    }

    /// <summary>The editable .conf files for this install, core ones first.</summary>
    public IReadOnlyList<ConfigFileInfo> ListFiles()
    {
        var s = _settings();
        var runDir = s.RunDirectory ?? s.DeployDirectory;
        var configDir = AcoreConfigReader.FindConfigDirectory(runDir);

        return AcoreConfigReader.FindConfigFiles(runDir)
            .Select(path =>
            {
                // Label module configs by their subfolder so "playerbots.conf" isn't ambiguous.
                var dir = Path.GetDirectoryName(path);
                var folder = dir != null && configDir != null && !PathsEqual(dir, configDir)
                    ? Path.GetFileName(dir) ?? ""
                    : "";
                return new ConfigFileInfo(path, Path.GetFileName(path), folder);
            })
            .ToList();
    }

    /// <summary>Read a config file's text. Throws nothing: failures come back as the message.</summary>
    public (bool Success, string Text, string Message) Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return (false, "", $"{path} no longer exists.");
            if (info.Length > MaxSizeBytes)
                return (false, "", $"{info.Name} is {info.Length / 1024 / 1024} MB — too large to edit here.");

            return (true, File.ReadAllText(path), "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.LogWarning(ex, "Could not read config {Path}", path);
            return (false, "", $"Could not read {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Write a config file, keeping a timestamped backup of the previous contents beside it.
    /// </summary>
    public ConfigSaveResult Save(string path, string text, string? timestamp = null)
    {
        var name = Path.GetFileName(path);
        try
        {
            if (!File.Exists(path))
                return new ConfigSaveResult(false, $"{name} no longer exists — refusing to create it.");

            var stamp = timestamp ?? DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = $"{path}.{stamp}.bak";

            // Copy rather than move: if the write fails, the original is still exactly where the server
            // expects it, and the .bak is a spare rather than the only copy.
            File.Copy(path, backupPath, overwrite: false);

            // AzerothCore's config parser is byte-oriented and its .conf files are plain ASCII/UTF-8; writing
            // a BOM here can make the very first setting unparseable, so encode without one.
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            _log.LogInformation("Saved config {Name}; previous contents at {Backup}", name, backupPath);
            return new ConfigSaveResult(true,
                $"Saved {name}. Previous version kept as {Path.GetFileName(backupPath)}. Restart the server to apply.",
                backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.LogWarning(ex, "Could not save config {Path}", path);
            return new ConfigSaveResult(false, $"Could not save {name}: {ex.Message}");
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
