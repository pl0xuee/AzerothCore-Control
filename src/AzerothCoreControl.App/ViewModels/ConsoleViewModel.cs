using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>The worldserver console: live output plus a command input piped to stdin.</summary>
public sealed partial class ConsoleViewModel : ObservableObject
{
    private const int MaxLines = 5000;

    private readonly ServerCoordinator _coordinator;

    // Server output arrives on background threads in bursts (thousands of lines at startup). Buffer it and
    // flush to the UI on a timer so the dispatcher isn't flooded (which would freeze the app).
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    [ObservableProperty] private string _commandInput = "";

    public ObservableCollection<string> Output { get; } = new();

    public ConsoleViewModel(ServerCoordinator coordinator)
    {
        _coordinator = coordinator;

        _coordinator.World.OutputLine += (kind, line) => Enqueue(kind, line);
        _coordinator.Auth.OutputLine += (kind, line) => Enqueue(kind, line);

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    private void Enqueue(ServerKind kind, string line)
    {
        var prefix = kind == ServerKind.Auth ? "[auth] " : "";
        _pending.Enqueue(prefix + line);
        // Hard cap the backlog so a runaway server can't grow memory without bound.
        while (_pending.Count > MaxLines * 4 && _pending.TryDequeue(out _)) { }
    }

    private void Flush()
    {
        if (_pending.IsEmpty)
            return;

        var batch = new List<string>();
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
            return;
        }

        foreach (var line in batch)
            Output.Add(line);
        var overflow = Output.Count - MaxLines;
        for (var i = 0; i < overflow; i++)
            Output.RemoveAt(0);
    }

    [RelayCommand]
    private void SendCommand()
    {
        var cmd = CommandInput.Trim();
        if (cmd.Length == 0)
            return;
        _coordinator.World.SendConsole(cmd);
        Output.Add("> " + cmd);
        CommandInput = "";
    }

    [RelayCommand]
    private void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        Output.Clear();
    }
}
