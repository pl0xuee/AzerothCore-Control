using System.Text.RegularExpressions;
using AzerothCoreControl.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace AzerothCoreControl.Core.Services;

/// <summary>Who's in the world right now: real players, and playerbots.</summary>
public sealed record WorldPopulation(int Players, int Bots)
{
    public int Total => Players + Bots;
}

/// <summary>
/// Counts the characters currently in the world, split into real players and playerbots.
/// </summary>
/// <remarks>
/// The database is the only honest source. The world server's console could be asked, but its command output
/// goes to stdout — which AzerothCore never flushes once it's a pipe (see <see cref="LogFileTailer"/>), so a
/// reply might arrive minutes later or not at all.
/// <para>
/// Bots are identified the way mod-playerbots itself does: every random bot lives on an account whose name
/// starts with <c>AiPlayerbot.RandomBotAccountPrefix</c> (default "rndbot"). Accounts live in the login
/// database and characters in the character database, so this joins across the two — which is also why it
/// reads each connection by its config KEY rather than using the flat backup list.
/// </para>
/// </remarks>
public sealed partial class WorldPopulationService
{
    private const string DefaultBotPrefix = "rndbot";

    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;

    public WorldPopulationService(Func<AppSettings> settings, ILogger<WorldPopulationService>? logger = null)
    {
        _settings = settings;
        _log = logger ?? NullLogger<WorldPopulationService>.Instance;
    }

    /// <summary>
    /// Query the population, or null if it can't be determined (not configured, MySQL down, server not up).
    /// Never throws: this feeds a dashboard tile, and a tile must not take the app down.
    /// </summary>
    public async Task<WorldPopulation?> QueryAsync(CancellationToken cancellationToken = default)
    {
        var s = _settings();
        var runDir = s.RunDirectory ?? s.DeployDirectory;

        var characters = AcoreConfigReader.FindDatabaseInfo(runDir, "CharacterDatabaseInfo");
        var login = AcoreConfigReader.FindDatabaseInfo(runDir, "LoginDatabaseInfo");
        if (characters == null || login == null)
            return null;

        // Database names are identifiers — they cannot be parameterised, so they are validated rather than
        // escaped. They come from a local .conf, but "it's a trusted file" is not a reason to interpolate an
        // unchecked string into SQL.
        if (!SafeIdentifier().IsMatch(characters.Database) || !SafeIdentifier().IsMatch(login.Database))
        {
            _log.LogWarning("Refusing to query: database name is not a plain identifier");
            return null;
        }

        var prefix = ReadBotAccountPrefix(runDir);

        var builder = new MySqlConnectionStringBuilder
        {
            Server = characters.Host,
            Port = (uint)characters.Port,
            UserID = characters.User,
            Password = characters.Password,
            Database = characters.Database,
            ConnectionTimeout = 5,      // a dashboard tile must never hang on a dead server
            DefaultCommandTimeout = 5,
        };

        try
        {
            await using var conn = new MySqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            // SUM(condition) counts matching rows — one pass, one round-trip, both numbers.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    COALESCE(SUM(a.username LIKE @prefix), 0)     AS bots,
                    COALESCE(SUM(a.username NOT LIKE @prefix), 0) AS players
                FROM `{characters.Database}`.characters c
                JOIN `{login.Database}`.account a ON a.id = c.account
                WHERE c.online = 1
                """;
            cmd.Parameters.AddWithValue("@prefix", prefix + "%");

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            var bots = Convert.ToInt32(reader.GetValue(0));
            var players = Convert.ToInt32(reader.GetValue(1));
            return new WorldPopulation(players, bots);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // MySQL down, wrong credentials, a module DB schema we don't expect — all just mean "unknown".
            _log.LogDebug(ex, "Could not query the world population");
            return null;
        }
    }

    /// <summary>
    /// mod-playerbots' bot account prefix, read from its own conf. Defaults to "rndbot", matching the module.
    /// </summary>
    internal string ReadBotAccountPrefix(string? runDirectory)
    {
        if (string.IsNullOrWhiteSpace(runDirectory))
            return DefaultBotPrefix;

        foreach (var conf in AcoreConfigReader.FindConfigFiles(runDirectory))
        {
            if (!Path.GetFileName(conf).Contains("playerbot", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = AcoreConfigReader.ReadKeyValues(conf).GetValueOrDefault("AiPlayerbot.RandomBotAccountPrefix");
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return DefaultBotPrefix;
    }

    /// <summary>A bare SQL identifier — letters, digits, underscore, dash. No backticks, no spaces, no dots.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex SafeIdentifier();
}
