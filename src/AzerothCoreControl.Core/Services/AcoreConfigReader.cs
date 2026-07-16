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
    private static readonly string[] DbKeys =
    {
        "LoginDatabaseInfo",      // acore_auth
        "CharacterDatabaseInfo",  // acore_characters
        "WorldDatabaseInfo",      // acore_world
    };

    /// <summary>Scan a run directory for worldserver/authserver config and extract MySQL details.</summary>
    public static AcoreDbDetection Detect(string? runDirectory)
    {
        if (string.IsNullOrWhiteSpace(runDirectory) || !Directory.Exists(runDirectory))
            return new AcoreDbDetection();

        var infos = new List<AcoreDbInfo>();
        // Prefer the user's live .conf; fall back to the shipped .conf.dist template.
        foreach (var confName in new[] { "worldserver.conf", "authserver.conf", "worldserver.conf.dist", "authserver.conf.dist" })
        {
            var path = Path.Combine(runDirectory, confName);
            if (!File.Exists(path))
                continue;
            foreach (var key in DbKeys)
            {
                var info = ReadDatabaseInfo(path, key);
                if (info != null)
                    infos.Add(info);
            }
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

    /// <summary>Parse a single <c>Key = "host;port;user;pass;db"</c> line from a conf file.</summary>
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

            var value = line[(eq + 1)..].Trim().Trim('"');
            var parts = value.Split(';');
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
        return null;
    }
}
