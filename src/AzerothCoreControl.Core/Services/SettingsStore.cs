using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothCoreControl.Core.Models;

namespace AzerothCoreControl.Core.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Thread-safe for concurrent saves.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    public SettingsStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <summary>Default location: <c>%AppData%\AzerothCoreControl\settings.json</c> (falls back to a local dir off-Windows).</summary>
    public static SettingsStore CreateDefault()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var dir = Path.Combine(baseDir, "AzerothCoreControl");
        return new SettingsStore(Path.Combine(dir, "settings.json"));
    }

    public string FilePath => _filePath;

    /// <summary>Reads settings from disk, or returns defaults if the file is missing/corrupt.</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return new AppSettings();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Corrupt file: don't crash — start from defaults (a Save will overwrite it).
                return new AppSettings();
            }
        }
    }

    /// <summary>Writes settings atomically (temp file + move) so a crash mid-write can't corrupt the file.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            // File.Move with overwrite is atomic on the same volume.
            File.Move(tmp, _filePath, overwrite: true);
        }
    }
}
