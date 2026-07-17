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

    // Server output arrives on background threads in bursts (thousands of lines at startup). Buffer it and
    // flush to the UI on a timer so the dispatcher isn't flooded (which would freeze the app).
    private readonly ConcurrentQueue<ConsoleLine> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    [ObservableProperty] private string _commandInput = "";
    [ObservableProperty] private int _lineCount;

    public string Title { get; }

    /// <summary>Only worldserver has an interactive console; authserver ignores stdin.</summary>
    public bool SupportsInput { get; }

    public ObservableCollection<ConsoleLine> Output { get; } = new();

    public ServerConsoleViewModel(ServerProcessSupervisor supervisor)
    {
        _supervisor = supervisor;
        Title = supervisor.Kind.DisplayName();
        SupportsInput = supervisor.Kind == ServerKind.World;

        _supervisor.OutputLine += (_, line) => Enqueue(ConsoleLine.FromServer(line));

        // DispatcherTimer defaults to DispatcherPriority.Background, which sits BELOW Input — during a
        // startup firehose the dispatcher is saturated with layout and the flush tick gets starved, so the
        // console visibly stalls exactly when output matters most. Showing server output is this app's job;
        // give it Normal. The work per tick is small (append a batch, trim the overflow).
        _flushTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
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
