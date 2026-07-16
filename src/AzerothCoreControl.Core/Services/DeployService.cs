using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public sealed record DeployResult(
    IReadOnlyList<string> UpdatedBinaries,
    IReadOnlyList<string> UpdatedConfigTemplates,
    IReadOnlyList<string> PreservedConfigs,
    IReadOnlyList<string> BackedUpFiles,
    IReadOnlyList<ConfigKeyDiff> NewConfigKeys);

/// <summary>A config key present in a new .conf.dist template but absent from the live .conf.</summary>
public sealed record ConfigKeyDiff(string ConfigFile, string Key, string DefaultValue);

/// <summary>
/// Copies freshly built binaries and updated <c>.conf.dist</c> templates from a build output directory
/// into the live run directory — WITHOUT ever overwriting the user's hand-edited <c>.conf</c> files.
///
/// This is the crux of the "keep my custom configs" requirement: only executables/libraries and
/// <c>*.conf.dist</c> templates are deployed; every <c>*.conf</c> is left byte-for-byte untouched.
/// Overwritten binaries are backed up as <c>&lt;name&gt;.bak</c> for rollback.
/// </summary>
public sealed class DeployService
{
    private static readonly string[] BinaryExtensions = { ".exe", ".dll", ".pdb" };

    private readonly ILogger _log;

    public DeployService(ILogger<DeployService>? logger = null)
        => _log = logger ?? NullLogger<DeployService>.Instance;

    /// <summary>
    /// Deploy from <paramref name="buildOutputDir"/> into <paramref name="runDir"/>.
    /// </summary>
    /// <param name="dryRun">When true, computes what would change without writing anything.</param>
    public DeployResult Deploy(string buildOutputDir, string runDir, bool dryRun = false)
    {
        if (!Directory.Exists(buildOutputDir))
            throw new DirectoryNotFoundException($"Build output directory not found: {buildOutputDir}");
        Directory.CreateDirectory(runDir);

        var updatedBinaries = new List<string>();
        var updatedTemplates = new List<string>();
        var preservedConfigs = new List<string>();
        var backedUp = new List<string>();

        foreach (var source in Directory.EnumerateFiles(buildOutputDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(source);

            // NEVER deploy a plain .conf — that is the user's file. Guard first, before anything else.
            if (IsUserConfig(name))
            {
                _log.LogDebug("Skipping user config {Name} (never overwritten).", name);
                continue;
            }

            var dest = Path.Combine(runDir, name);

            if (IsConfigTemplate(name))
            {
                if (FilesDiffer(source, dest))
                {
                    if (!dryRun) CopyWithBackup(source, dest, backedUp);
                    updatedTemplates.Add(name);
                }
            }
            else if (IsBinary(name))
            {
                if (FilesDiffer(source, dest))
                {
                    if (!dryRun) CopyWithBackup(source, dest, backedUp);
                    updatedBinaries.Add(name);
                }
            }
            // Anything else (readmes, cmake junk) is ignored.
        }

        // Record which live configs we deliberately preserved (for the UI summary).
        foreach (var conf in Directory.EnumerateFiles(runDir, "*.conf", SearchOption.TopDirectoryOnly))
            preservedConfigs.Add(Path.GetFileName(conf));

        var newKeys = DiffNewConfigKeys(runDir);

        return new DeployResult(updatedBinaries, updatedTemplates, preservedConfigs, backedUp, newKeys);
    }

    /// <summary>
    /// Report config keys that exist in the shipped <c>.conf.dist</c> template but are missing from the
    /// matching live <c>.conf</c>, so the UI can suggest (never auto-apply) newly added settings.
    /// </summary>
    public IReadOnlyList<ConfigKeyDiff> DiffNewConfigKeys(string runDir)
    {
        var diffs = new List<ConfigKeyDiff>();
        if (!Directory.Exists(runDir))
            return diffs;

        foreach (var distPath in Directory.EnumerateFiles(runDir, "*.conf.dist", SearchOption.TopDirectoryOnly))
        {
            var confPath = distPath[..^".dist".Length]; // strip ".dist"
            if (!File.Exists(confPath))
                continue;

            var liveKeys = ParseConfigKeys(confPath).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in ParseConfigKeys(distPath))
            {
                if (!liveKeys.Contains(key))
                    diffs.Add(new ConfigKeyDiff(Path.GetFileName(confPath), key, value));
            }
        }
        return diffs;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>True for a plain <c>*.conf</c> (but NOT <c>*.conf.dist</c>).</summary>
    internal static bool IsUserConfig(string fileName)
        => fileName.EndsWith(".conf", StringComparison.OrdinalIgnoreCase);

    internal static bool IsConfigTemplate(string fileName)
        => fileName.EndsWith(".conf.dist", StringComparison.OrdinalIgnoreCase);

    internal static bool IsBinary(string fileName)
        => BinaryExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    private static bool FilesDiffer(string source, string dest)
    {
        if (!File.Exists(dest))
            return true;
        var a = new FileInfo(source);
        var b = new FileInfo(dest);
        if (a.Length != b.Length)
            return true;
        return !FileContentsEqual(source, dest);
    }

    private static bool FileContentsEqual(string a, string b)
    {
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        Span<byte> ba = stackalloc byte[8192];
        Span<byte> bb = stackalloc byte[8192];
        int ra;
        while ((ra = sa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false)) > 0)
        {
            int rb = sb.ReadAtLeast(bb, ra, throwOnEndOfStream: false);
            if (ra != rb || !ba[..ra].SequenceEqual(bb[..rb]))
                return false;
        }
        return sb.ReadByte() == -1; // both fully consumed
    }

    private void CopyWithBackup(string source, string dest, List<string> backedUp)
    {
        if (File.Exists(dest))
        {
            var bak = dest + ".bak";
            File.Copy(dest, bak, overwrite: true);
            backedUp.Add(Path.GetFileName(dest));
        }
        File.Copy(source, dest, overwrite: true);
        _log.LogInformation("Deployed {File}", Path.GetFileName(dest));
    }

    /// <summary>Parse "Key = Value" lines from an AzerothCore .conf/.conf.dist file (ignores comments/sections).</summary>
    private static Dictionary<string, string> ParseConfigKeys(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('[') || !line.Contains('='))
                continue;
            var idx = line.IndexOf('=');
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key.Length > 0)
                result[key] = value;
        }
        return result;
    }
}
