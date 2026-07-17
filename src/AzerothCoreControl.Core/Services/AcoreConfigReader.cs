namespace AzerothCoreControl.Core.Services;

/// <summary>MySQL connection details parsed from an AzerothCore <c>*DatabaseInfo</c> config line.</summary>
public sealed record AcoreDbInfo(string Host, int Port, string User, string Password, string Database);

/// <summary>Result of scanning a run directory's config for its MySQL databases.</summary>
public sealed class AcoreDbDetection
{
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? User { get; init; }
    public string? Password { get; init; }

    /// <summary>Detected database names (auth, world, characters), de-duplicated and in a stable order.</summary>
    public List<string> Databases { get; init; } = new();

    public bool Found => Databases.Count > 0;
}

/// <summary>
/// Reads AzerothCore's own config files to auto-detect the MySQL connection and database names, so the
/// user doesn't have to type them. AzerothCore stores each connection as
/// <c>LoginDatabaseInfo = "host;port;user;pass;dbname"</c> in worldserver/authserver .conf.
/// </summary>
public static class AcoreConfigReader
{
    /// <summary>
    /// Config keys holding a connection string all end in <c>DatabaseInfo</c> — <c>LoginDatabaseInfo</c>,
    /// <c>CharacterDatabaseInfo</c>, <c>WorldDatabaseInfo</c>, plus whatever modules add (playerbots
    /// contributes <c>PlayerbotsDatabaseInfo</c>). Matching the suffix rather than a fixed list means a
    /// module's database is detected without this code knowing about the module.
    /// </summary>
    private const string DbKeySuffix = "DatabaseInfo";

    /// <summary>Scan a run directory for worldserver/authserver config and extract MySQL details.</summary>
    public static AcoreDbDetection Detect(string? runDirectory)
    {
        if (string.IsNullOrWhiteSpace(runDirectory) || !Directory.Exists(runDirectory))
            return new AcoreDbDetection();

        var infos = new List<AcoreDbInfo>();
        // Only the user's real .conf files — NEVER the .conf.dist templates, which hold placeholder
        // credentials/database names rather than the actual configured database.
        foreach (var dir in ConfigDirectories(runDirectory))
        foreach (var confName in new[] { "worldserver.conf", "authserver.conf" })
        {
            var path = Path.Combine(dir, confName);
            if (File.Exists(path))
                infos.AddRange(ReadAllDatabaseInfos(path));
        }

        if (infos.Count == 0)
            return new AcoreDbDetection();

        var first = infos[0];
        var databases = infos
            .Select(i => i.Database)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AcoreDbDetection
        {
            Host = first.Host,
            Port = first.Port,
            User = first.User,
            Password = first.Password,
            Databases = databases,
        };
    }

    /// <summary>
    /// Directories that may hold the .conf, nearest-first. The run directory is where worldserver.exe
    /// lives, but AzerothCore's Windows layout keeps configs beside the binaries rather than with them
    /// (<c>env/dist/bin/worldserver.exe</c> vs <c>env/dist/etc/worldserver.conf</c>), so the sibling and
    /// child "etc"/"configs" folders have to be searched too.
    /// </summary>
    private static IEnumerable<string> ConfigDirectories(string runDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string> { runDirectory };
        try
        {
            var parent = Directory.GetParent(runDirectory)?.FullName;
            if (parent != null)
                roots.Add(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        foreach (var root in roots)
        foreach (var sub in new[] { "", "etc", "configs" })
        {
            var dir = sub.Length == 0 ? root : Path.Combine(root, sub);
            if (seen.Add(dir) && Directory.Exists(dir))
                yield return dir;
        }
    }

    /// <summary>Parse every <c>*DatabaseInfo = "host;port;user;pass;db"</c> line in a conf file, in file order.</summary>
    public static IEnumerable<AcoreDbInfo> ReadAllDatabaseInfos(string confPath)
    {
        foreach (var raw in File.ReadLines(confPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;

            var key = line[..eq].Trim();
            if (!key.EndsWith(DbKeySuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var info = ParseValue(line[(eq + 1)..]);
            if (info != null)
                yield return info;
        }
    }

    /// <summary>Parse the first <c>Key = "host;port;user;pass;db"</c> line matching <paramref name="key"/>.</summary>
    public static AcoreDbInfo? ReadDatabaseInfo(string confPath, string key)
    {
        foreach (var raw in File.ReadLines(confPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            if (!string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                continue;

            return ParseValue(line[(eq + 1)..]);
        }
        return null;
    }

    /// <summary>Parse a <c>"host;port;user;pass;db"</c> value, or null if it isn't a connection string.</summary>
    private static AcoreDbInfo? ParseValue(string rawValue)
    {
        var parts = rawValue.Trim().Trim('"').Split(';');
        if (parts.Length < 5)
            return null;

        var host = parts[0].Trim();
        var port = int.TryParse(parts[1].Trim(), out var p) ? p : 3306;
        var user = parts[2].Trim();
        var pass = parts[3];
        var db = parts[4].Trim();
        if (string.IsNullOrWhiteSpace(db))
            return null;
        return new AcoreDbInfo(host, port, user, pass, db);
    }
}
