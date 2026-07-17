using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Live output for a single server, plus (for worldserver) a command line piped to its stdin.</summary>
public sealed partial class ServerConsoleViewModel : ObservableObject
{
    private const int MaxLines = 5000;

    private readonly ServerProcessSupervisor _supervisor;
    private readonly Func<AppSettings> _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    /// <summary>Guards <see cref="_tailer"/>: attaching races between the ctor and a background Started event.</summary>
    private readonly object _tailGate = new();

    /// <summary>Set once we're following the server's log file instead of its stdout.</summary>
    private LogFileTailer? _tailer;

    /// <summary>
    /// Read by the OutputLine handler on the process's own thread, so it needs a barrier — a stale false
    /// there means stdout lines get duplicated on top of the log's.
    /// </summary>
    private volatile bool _tailing;

    // Server output arrives on background threads in bursts (thousands of lines at startup). Buffer it and
    // flush to the UI on a timer so the dispatcher isn't flooded (which would freeze the app).
    private readonly ConcurrentQueue<ConsoleLine> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    [ObservableProperty] private string _commandInput = "";

    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [ObservableProperty] private int _lineCount;

    /// <summary>Drives the "nothing here yet" hint — a blank pane that explains nothing is a support ticket.</summary>
    public bool IsEmpty => LineCount == 0;

    public string Title { get; }

    /// <summary>Only worldserver has an interactive console; authserver ignores stdin.</summary>
    public bool SupportsInput { get; }

    /// <summary>Shown while this console has produced nothing — a blank pane that explains nothing is a support ticket.</summary>
    [ObservableProperty] private string _emptyHint = "No output yet.\nStart the server to see its output here.";

    /// <summary>The log file being followed, or null when reading the process's stdout.</summary>
    [ObservableProperty] private string? _logFilePath;

    public ObservableCollection<ConsoleLine> Output { get; } = new();

    public ServerConsoleViewModel(ServerProcessSupervisor supervisor, Func<AppSettings> settings)
    {
        _supervisor = supervisor;
        _settings = settings;
        Title = supervisor.Kind.DisplayName();
        SupportsInput = supervisor.Kind == ServerKind.World;

        _supervisor.OutputLine += (_, line) =>
        {
            // When tailing, stdout is ignored: the server's console appender never flushes, so these same
            // lines arrive late in 4KB gluts and would duplicate everything the log already showed.
            if (!_tailing)
                Enqueue(ConsoleLine.FromServer(line));
        };

        // Lifecycle events belong in the console too, so a quiet server is never indistinguishable from a
        // broken app.
        _supervisor.Notable += e =>
        {
            Enqueue(new ConsoleLine(DateTime.Now, e.Message, ConsoleSeverity.System));

            // A start is when the log file appears (and is truncated), and — with the timestamp flag — when
            // its NAME changes, so this is the moment to (re)bind.
            //
            // Off-thread on purpose: Notable is raised while the supervisor holds its lock, and resolving the
            // log path reads the .conf off disk. The UI thread takes that same lock every second to sample
            // CPU/memory, so doing this inline would stall the window on every start and restart.
            if (e.Kind == SupervisorEventKind.Started)
                _ = Task.Run(TryAttachTail);
        };

        _ = Task.Run(TryAttachTail);
        UpdateEmptyHint();

        // DispatcherTimer defaults to DispatcherPriority.Background, which sits BELOW Input — during a
        // startup firehose the dispatcher is saturated with layout and the flush tick gets starved, so the
        // console visibly stalls exactly when output matters most. Showing server output is this app's job;
        // give it Normal. The work per tick is small (append a batch, trim the overflow).
        _flushTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    /// <summary>
    /// Follow the server's log file rather than its stdout, if we can find it.
    /// </summary>
    /// <remarks>
    /// AzerothCore's console appender never fflushes, so once stdout is a pipe the C runtime full-buffers it
    /// at 4KB. worldserver is loud enough to keep filling that; authserver emits a few hundred bytes and then
    /// nothing, so its output can sit undelivered indefinitely — the pane looks broken while the server is
    /// perfectly healthy. The FILE appender fflushes every line, so the log is the only live source.
    /// </remarks>
    private void TryAttachTail()
    {
        string? path;
        try
        {
            var s = _settings();
            path = AcoreLogLocator.FindLogFile(s.RunDirectory ?? s.DeployDirectory, _supervisor.Kind);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not locate the {Server} log file", _supervisor.Kind);
            return;
        }

        if (path == null)
            return;

        LogFileTailer? previous;
        LogFileTailer tailer;
        lock (_tailGate)
        {
            // Already following this exact file — nothing to do. Compared by PATH, not just "do we have a
            // tailer": with the timestamp flag every launch writes a NEW file, so keeping the old tailer
            // would leave the pane following a dead file forever, with stdout suppressed as well.
            if (_tailer != null && string.Equals(_tailer.Path, path, StringComparison.OrdinalIgnoreCase))
                return;

            previous = _tailer;
            tailer = new LogFileTailer(path);
            tailer.LineRead += line => Enqueue(ConsoleLine.FromServer(line));
            _tailer = tailer;
            _tailing = true;
        }

        previous?.Dispose();
        tailer.Start();

        // These are bound properties and this runs off the UI thread.
        _dispatcher.BeginInvoke(() =>
        {
            LogFilePath = path;
            UpdateEmptyHint();
        });
    }

    private void UpdateEmptyHint()
    {
        EmptyHint = LogFilePath != null
            ? $"No output yet.\nFollowing {LogFilePath} — output appears here once the server writes to it."
            : "No output yet.\nStart the server to see its output here.\n\nAzerothCore only flushes its console "
              + "output in 4KB blocks once it's captured, so a quiet server can appear silent for a long time. "
              + "Set the Run directory in Settings and this will follow the server's log file instead, which is "
              + "written line by line.";
    }

    private void Enqueue(ConsoleLine line)
    {
        _pending.Enqueue(line);
        // Hard cap the backlog so a runaway server can't grow memory without bound.
        while (_pending.Count > MaxLines * 4 && _pending.TryDequeue(out _)) { }
    }

    private void Flush()
    {
        if (_pending.IsEmpty)
            return;

        var batch = new List<ConsoleLine>();
        while (_pending.TryDequeue(out var line))
            batch.Add(line);
        if (batch.Count == 0)
            return;

        if (batch.Count >= MaxLines)
        {
            // Massive burst (server startup): only the newest MaxLines matter — rebuild once instead of
            // raising thousands of collection-changed events.
            Output.Clear();
            for (var i = batch.Count - MaxLines; i < batch.Count; i++)
                Output.Add(batch[i]);
            LineCount = Output.Count;
            return;
        }

        foreach (var line in batch)
            Output.Add(line);
        var overflow = Output.Count - MaxLines;
        for (var i = 0; i < overflow; i++)
            Output.RemoveAt(0);
        LineCount = Output.Count;
    }

    [RelayCommand]
    private void SendCommand()
    {
        var cmd = CommandInput.Trim();
        if (cmd.Length == 0 || !SupportsInput)
            return;
        _supervisor.SendConsole(cmd);
        Output.Add(new ConsoleLine(DateTime.Now, cmd, ConsoleSeverity.Command));
        LineCount = Output.Count;
        CommandInput = "";
    }

    [RelayCommand]
    private void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        Output.Clear();
        LineCount = 0;
    }
}
