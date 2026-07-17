using System.Text;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

/// <summary>
/// The tailer is polled directly rather than via its timer, so these are deterministic.
/// </summary>
public class LogFileTailerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _log;
    private readonly List<string> _lines = new();
    private readonly LogFileTailer _tailer;

    public LogFileTailerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "acc-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _log = Path.Combine(_dir, "Auth.log");
        _tailer = new LogFileTailer(_log);
        _tailer.LineRead += l => _lines.Add(l);
    }

    public void Dispose()
    {
        _tailer.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Append(string text) => File.AppendAllText(_log, text);

    [Fact]
    public void ReadsAppendedLines()
    {
        Append("first\nsecond\n");
        _tailer.Poll();

        Assert.Equal(new[] { "first", "second" }, _lines);
    }

    [Fact]
    public void ReadsOnlyWhatIsNew()
    {
        Append("first\n");
        _tailer.Poll();
        _lines.Clear();

        Append("second\n");
        _tailer.Poll();

        Assert.Equal(new[] { "second" }, _lines);
    }

    [Fact]
    public void HoldsBackAPartialLineUntilItsNewlineArrives()
    {
        // The server writes a line in pieces; reporting half of it would be worse than waiting.
        Append("half");
        _tailer.Poll();
        Assert.Empty(_lines);

        Append("-a-line\n");
        _tailer.Poll();
        Assert.Equal(new[] { "half-a-line" }, _lines);
    }

    [Fact]
    public void AMultiByteCharacterSplitAcrossPolls_IsNotCorrupted()
    {
        // The writer flushes BYTES, not characters, so a poll can land mid-sequence. A stateless decode turns
        // both halves into replacement junk — permanently, for that line. Log text carries player names, so
        // this is not hypothetical. "é" is 0xC3 0xA9.
        var eAcute = Encoding.UTF8.GetBytes("é");
        Assert.Equal(2, eAcute.Length);

        File.WriteAllBytes(_log, Encoding.UTF8.GetBytes("caf").Concat(eAcute.Take(1)).ToArray());
        _tailer.Poll();
        Assert.Empty(_lines); // incomplete character AND no newline yet

        using (var s = new FileStream(_log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            s.Write(eAcute.Skip(1).Concat(Encoding.UTF8.GetBytes("\n")).ToArray());
        _tailer.Poll();

        Assert.Equal(new[] { "café" }, _lines);
    }

    [Fact]
    public void HandlesCrLf()
    {
        Append("windows\r\nlines\r\n");
        _tailer.Poll();

        Assert.Equal(new[] { "windows", "lines" }, _lines);
    }

    [Fact]
    public void TruncationOnRestart_StartsOver()
    {
        // AzerothCore opens the log with mode "w", truncating it on every launch. Without noticing, we'd
        // resume from a byte offset in the middle of the NEW run's output and miss its startup lines.
        Append("old run line 1\nold run line 2\n");
        _tailer.Poll();
        _lines.Clear();

        File.WriteAllText(_log, "new run\n"); // truncate + write
        _tailer.Poll();

        Assert.Equal(new[] { "new run" }, _lines);
    }

    [Fact]
    public void MissingFile_IsNotAnError_AndIsPickedUpWhenItAppears()
    {
        _tailer.Poll(); // no file yet — the server hasn't started
        Assert.Empty(_lines);

        Append("now it exists\n");
        _tailer.Poll();

        Assert.Equal(new[] { "now it exists" }, _lines);
    }

    [Fact]
    public void DoesNotLockTheFileAgainstTheServer()
    {
        // A supervisor must never be the reason a server can't write its own log.
        Append("line\n");
        _tailer.Poll();

        using var writer = new FileStream(_log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer.Write(Encoding.UTF8.GetBytes("written while tailing\n"));
        writer.Flush();

        _tailer.Poll();
        Assert.Contains("written while tailing", _lines);
    }

    [Fact]
    public void StartFromEnd_SkipsExistingContent()
    {
        Append("existing\n");
        using var tailer = new LogFileTailer(_log);
        var seen = new List<string>();
        tailer.LineRead += seen.Add;

        tailer.Start(fromStart: false);
        Append("new\n");
        tailer.Poll();

        Assert.Equal(new[] { "new" }, seen);
    }
}

public class AcoreLogLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _runDir;

    public AcoreLogLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acc-loglocate-" + Guid.NewGuid().ToString("N"));
        _runDir = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_runDir);
        Directory.CreateDirectory(Path.Combine(_root, "etc"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteAuthConf(string body) => File.WriteAllText(Path.Combine(_root, "etc", "authserver.conf"), body);

    /// <summary>The stock AzerothCore logging block.</summary>
    private const string DefaultLogging = """
        LogsDir = ""
        Appender.Console=1,5,0,"1 9 3 6 5 8"
        Appender.Auth=2,5,0,Auth.log,w
        Logger.root=4,Console Auth
        """;

    [Fact]
    public void FindsTheLogFileFromTheStockConfig()
    {
        WriteAuthConf(DefaultLogging);

        var path = AcoreLogLocator.FindLogFile(_runDir, ServerKind.Auth);

        // LogsDir empty means "the server's working directory".
        Assert.Equal(Path.Combine(_runDir, "Auth.log"), path);
    }

    [Fact]
    public void HonoursAnAbsoluteLogsDir()
    {
        // The path goes in verbatim: AzerothCore's config reader does not unescape backslashes, so a Windows
        // user writes C:\Logs, not C:\\Logs. (Escaping it here passed on Linux — no backslashes to double —
        // and failed on Windows, which is what the release build caught.)
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        WriteAuthConf($"""
            LogsDir = "{logs}"
            Appender.Auth=2,5,0,Auth.log,w
            Logger.root=4,Console Auth
            """);

        Assert.Equal(Path.Combine(logs, "Auth.log"), AcoreLogLocator.FindLogFile(_runDir, ServerKind.Auth));
    }

    [Fact]
    public void HonoursARelativeLogsDir()
    {
        Directory.CreateDirectory(Path.Combine(_runDir, "logs"));
        WriteAuthConf("""
            LogsDir = "logs"
            Appender.Auth=2,5,0,Auth.log,w
            Logger.root=4,Console Auth
            """);

        Assert.Equal(Path.Combine(_runDir, "logs", "Auth.log"), AcoreLogLocator.FindLogFile(_runDir, ServerKind.Auth));
    }

    [Fact]
    public void PicksTheNewestFile_WhenTheNameCarriesATimestamp()
    {
        // Flags bit 8 = "append timestamp to the log file name", so each launch writes a new file.
        WriteAuthConf("""
            LogsDir = ""
            Appender.Auth=2,5,8,Auth.log,w
            Logger.root=4,Console Auth
            """);
        var older = Path.Combine(_runDir, "Auth_2026-07-16_10-00-00.log");
        var newer = Path.Combine(_runDir, "Auth_2026-07-17_05-31-02.log");
        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "new");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 7, 17, 5, 31, 2, DateTimeKind.Utc));

        Assert.Equal(newer, AcoreLogLocator.FindLogFile(_runDir, ServerKind.Auth));
    }

    [Fact]
    public void IgnoresAppendersTheRootLoggerDoesNotUse()
    {
        // A file appender that's configured but not wired to root receives nothing — following it would show
        // an empty pane forever.
        WriteAuthConf("""
            LogsDir = ""
            Appender.Unused=2,5,0,Unused.log,w
            Appender.Auth=2,5,0,Auth.log,w
            Logger.root=4,Console Auth
            """);

        Assert.Equal(Path.Combine(_runDir, "Auth.log"), AcoreLogLocator.FindLogFile(_runDir, ServerKind.Auth));
    }

    [Fact]
    public void ConsoleOnlyLogging_HasNoFileToFollow()
    {
        WriteAuthConf("""
            LogsDir = ""
            Appender.Console=1,5,0
            Logger.root=4,Console
            """);

        Assert.Null(AcoreLogLocator.FindLogFile(_runDir, ServerKind.Auth));
    }

    [Fact]
    public void NoConfig_IsNull()
    {
        Assert.Null(AcoreLogLocator.FindLogFile(_runDir, ServerKind.Auth));
        Assert.Null(AcoreLogLocator.FindLogFile(null, ServerKind.Auth));
    }

    [Fact]
    public void ReadsTheAuthListenPort()
    {
        WriteAuthConf("RealmServerPort = 3724\n" + DefaultLogging);
        Assert.Equal(3724, AcoreConfigReader.FindListenPort(_runDir, ServerKind.Auth));
    }

    [Fact]
    public void ReadsTheWorldListenPort()
    {
        File.WriteAllText(Path.Combine(_root, "etc", "worldserver.conf"), "WorldServerPort = 8085\n");
        Assert.Equal(8085, AcoreConfigReader.FindListenPort(_runDir, ServerKind.World));
    }

    [Fact]
    public void ANonDefaultPortIsRead_NotAssumed()
    {
        // The whole point of showing it: this server is not on 3724 and nothing else would tell you.
        WriteAuthConf("RealmServerPort = 3725\n" + DefaultLogging);
        Assert.Equal(3725, AcoreConfigReader.FindListenPort(_runDir, ServerKind.Auth));
    }

    [Fact]
    public void AMissingOrJunkPort_IsNullRatherThanAGuess()
    {
        WriteAuthConf(DefaultLogging);
        Assert.Null(AcoreConfigReader.FindListenPort(_runDir, ServerKind.Auth));

        WriteAuthConf("RealmServerPort = not-a-number\n" + DefaultLogging);
        Assert.Null(AcoreConfigReader.FindListenPort(_runDir, ServerKind.Auth));
    }

    [Fact]
    public void CommentedOutSettingsAreIgnored()
    {
        WriteAuthConf("#RealmServerPort = 9999\nRealmServerPort = 3724\n" + DefaultLogging);
        Assert.Equal(3724, AcoreConfigReader.FindListenPort(_runDir, ServerKind.Auth));
    }
}
