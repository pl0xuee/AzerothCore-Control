using AzerothCoreControl.Core.Models;

namespace AzerothCoreControl.Core.Services;

/// <summary>
/// Works out which file a server logs to, by reading its .conf the way AzerothCore does.
/// </summary>
/// <remarks>
/// Needed because a server's stdout is unusable when captured: AzerothCore's console appender never calls
/// fflush, so once stdout is a pipe the C runtime full-buffers it (4KB) and quiet servers — authserver
/// especially — deliver nothing for long stretches. Its FILE appender fflushes every line, so the log file is
/// always current and is the only reliable source of live output.
/// <para>
/// Config shape: <c>LogsDir = ""</c> (empty = the server's working directory) and
/// <c>Appender.Name = Type,LogLevel,Flags,filename,mode</c> where Type 2 is a file appender, and Flags bit 8
/// means "append a timestamp to the file name". <c>Logger.root = Level,Appender1 Appender2</c> says which
/// appenders are actually in use.
/// </para>
/// </remarks>
public static class AcoreLogLocator
{
    private const int FileAppenderType = 2;
    private const int TimestampFileNameFlag = 8;

    /// <summary>
    /// The log file <paramref name="kind"/> writes to, or null if it can't be determined. The file need not
    /// exist yet — the server may not have started.
    /// </summary>
    public static string? FindLogFile(string? runDirectory, ServerKind kind)
    {
        if (string.IsNullOrWhiteSpace(runDirectory))
            return null;

        var confPath = AcoreConfigReader.FindServerConfig(runDirectory, kind);
        if (confPath == null)
            return null;

        var settings = AcoreConfigReader.ReadKeyValues(confPath);
        if (settings.Count == 0)
            return null;

        var (fileName, flags) = FindRootFileAppender(settings);
        if (fileName == null)
            return null;

        // LogsDir is relative to the server's working directory when not absolute; empty means exactly that.
        var logsDir = settings.GetValueOrDefault("LogsDir", "") ?? "";
        var dir = logsDir.Length == 0
            ? runDirectory
            : Path.IsPathRooted(logsDir) ? logsDir : Path.Combine(runDirectory, logsDir);

        if (!Directory.Exists(dir))
            return null;

        return (flags & TimestampFileNameFlag) != 0
            ? NewestTimestamped(dir, fileName)
            : Path.Combine(dir, fileName);
    }

    /// <summary>The file appender that Logger.root actually uses, as (fileName, flags).</summary>
    private static (string? FileName, int Flags) FindRootFileAppender(IReadOnlyDictionary<string, string> settings)
    {
        // "Logger.root = 4,Console Auth" — level, then the appender names it writes to.
        var root = settings.GetValueOrDefault("Logger.root");
        var names = root?.Split(',', StringSplitOptions.TrimEntries).Skip(1).FirstOrDefault()
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();

        foreach (var name in names)
        {
            if (!settings.TryGetValue($"Appender.{name}", out var spec))
                continue;

            // Type,LogLevel,Flags,filename,mode
            var fields = spec.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 4)
                continue;
            if (!int.TryParse(fields[0], out var type) || type != FileAppenderType)
                continue;

            int.TryParse(fields[2], out var flags);
            var fileName = Unquote(fields[3]);
            if (!string.IsNullOrWhiteSpace(fileName))
                return (fileName, flags);
        }

        return (null, 0);
    }

    /// <summary>
    /// With the timestamp flag the server writes e.g. <c>Auth_2026-07-17_05-31-02.log</c>, a new file per
    /// launch — so the live one is simply the newest match.
    /// </summary>
    private static string? NewestTimestamped(string dir, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        try
        {
            return new DirectoryInfo(dir)
                .GetFiles($"{stem}*{ext}")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Unquote(string value) => AcoreConfigReader.Unquote(value);
}
