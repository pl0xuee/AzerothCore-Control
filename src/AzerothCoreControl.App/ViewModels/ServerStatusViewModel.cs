using System.Windows.Threading;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Live status of one supervised server, bound by a dashboard card.</summary>
public sealed partial class ServerStatusViewModel : ObservableObject
{
    private readonly ServerProcessSupervisor _supervisor;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty] private ServerState _state;
    [ObservableProperty] private int _restartCount;
    [ObservableProperty] private string _uptime = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuText))]
    private double _cpuPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText))]
    private double _memoryMb;

    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastSample;

    public ServerStatusViewModel(ServerProcessSupervisor supervisor)
    {
        _supervisor = supervisor;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _state = supervisor.State;

        supervisor.StateChanged += (_, s) => _dispatcher.BeginInvoke(() =>
        {
            State = s;
            RestartCount = supervisor.RestartCount;
        });
    }

    public string Name => _supervisor.Kind.DisplayName();
    public ServerKind Kind => _supervisor.Kind;

    /// <summary>Recompute the uptime string; call periodically from a UI timer.</summary>
    public void RefreshUptime()
    {
        var since = _supervisor.RunningSince;
        Uptime = since is { } s ? FormatDuration(DateTimeOffset.UtcNow - s) : "—";
    }

    /// <summary>
    /// Sample CPU%/memory once per tick. CPU% is the share of one core's time used since the last
    /// sample, normalized across all logical processors (0–100%).
    /// </summary>
    public void RefreshResources()
    {
        if (_supervisor.TryGetResourceSnapshot(out var ws, out var cpu))
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastSample != default)
            {
                var wallSeconds = (now - _lastSample).TotalSeconds;
                if (wallSeconds > 0)
                {
                    var cpuSeconds = (cpu - _lastCpuTime).TotalSeconds;
                    var pct = 100.0 * cpuSeconds / (wallSeconds * Environment.ProcessorCount);
                    CpuPercent = Math.Clamp(pct, 0, 100);
                }
            }
            _lastCpuTime = cpu;
            _lastSample = now;
            MemoryMb = ws / (1024.0 * 1024.0);
        }
        else
        {
            CpuPercent = 0;
            MemoryMb = 0;
            _lastSample = default;
        }
    }

    public string CpuText => IsRunning ? $"{CpuPercent:0}%" : "—";

    public string MemoryText => !IsRunning || MemoryMb <= 0
        ? "—"
        : MemoryMb >= 1024 ? $"{MemoryMb / 1024.0:0.0} GB" : $"{MemoryMb:0} MB";

    public bool IsRunning => State == ServerState.Running;

    partial void OnStateChanged(ServerState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(MemoryText));
    }

    public string StatusText => State switch
    {
        ServerState.Running => "Running",
        ServerState.Stopped => "Stopped",
        ServerState.Starting => "Starting…",
        ServerState.Restarting => "Restarting…",
        ServerState.Crashed => "Crashed",
        _ => State.ToString(),
    };

    private static string FormatDuration(TimeSpan t)
        => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
         : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m"
         : $"{t.Minutes}m {t.Seconds}s";
}
