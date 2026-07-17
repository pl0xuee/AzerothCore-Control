using System.Text;

namespace AzerothCoreControl.Core.Services;

/// <summary>
/// Follows a growing log file and raises each newly appended line — <c>tail -f</c>.
/// </summary>
/// <remarks>
/// Polled rather than FileSystemWatcher-driven: appends to an open file don't reliably raise Changed events
/// (the directory entry needn't be updated until close), so a watcher would miss exactly the writes this
/// exists to catch.
/// <para>
/// Opened with FileShare.ReadWrite | FileShare.Delete so the server can keep writing, rotate, or delete the
/// file while we read it — a supervisor must never be the reason a server can't log.
/// </para>
/// </remarks>
public sealed class LogFileTailer : IDisposable
{
    private readonly string _path;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();

    private Timer? _timer;
    private long _position;
    private string _partial = "";
    private bool _disposed;

    // Stateful on purpose. A poll can end mid-character — the writer flushes bytes, not characters — and
    // Encoding.UTF8.GetString would turn each half of a split sequence into replacement junk. A Decoder holds
    // the incomplete tail until the rest of it arrives.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    /// <summary>A complete line appended to the file.</summary>
    public event Action<string>? LineRead;

    public LogFileTailer(string path, TimeSpan? interval = null)
    {
        _path = path;
        _interval = interval ?? TimeSpan.FromMilliseconds(300);
    }

    public string Path => _path;

    /// <summary>
    /// Begin following. <paramref name="fromStart"/> replays existing content — the file is truncated on each
    /// launch (mode "w"), so its contents are this run's output and worth showing.
    /// </summary>
    public void Start(bool fromStart = true)
    {
        lock (_gate)
        {
            if (_timer != null || _disposed)
                return;
            if (!fromStart)
                _position = CurrentLength();
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, _interval);
        }
    }

    /// <summary>Read whatever has been appended since last time and raise a line for each. Safe to call spuriously.</summary>
    internal void Poll()
    {
        List<string> lines;
        lock (_gate)
        {
            if (_disposed)
                return;
            lines = ReadNewLinesLocked();
        }

        // Raised outside the lock: a handler is free to do slow work without stalling the next poll.
        foreach (var line in lines)
            LineRead?.Invoke(line);
    }

    private List<string> ReadNewLinesLocked()
    {
        var lines = new List<string>();
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                // Not written yet, or deleted. Rewind so a recreated file is read from its start.
                _position = 0;
                _partial = "";
                _decoder.Reset(); // drop any half-character from the previous run
                return lines;
            }

            // Shorter than where we were: the server restarted and truncated it (mode "w"). Start over, or
            // we'd read from the middle of the new run's output.
            if (info.Length < _position)
            {
                _position = 0;
                _partial = "";
                _decoder.Reset(); // drop any half-character from the previous run
            }
            if (info.Length == _position)
                return lines;

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_position, SeekOrigin.Begin);

            var buffer = new byte[info.Length - _position];
            var read = stream.Read(buffer, 0, buffer.Length);
            _position += read;

            // flush: false — anything incomplete stays in the decoder for the next poll.
            var chars = new char[_decoder.GetCharCount(buffer, 0, read, flush: false)];
            var charCount = _decoder.GetChars(buffer, 0, read, chars, 0, flush: false);

            var text = _partial + new string(chars, 0, charCount);
            _partial = "";

            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                    continue;
                var line = text[start..i].TrimEnd('\r');
                lines.Add(line);
                start = i + 1;
            }

            // A trailing fragment means the writer is mid-line; hold it until its newline arrives rather than
            // reporting half a line.
            if (start < text.Length)
                _partial = text[start..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or vanished mid-read — the next poll picks it up.
        }
        return lines;
    }

    private long CurrentLength()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
