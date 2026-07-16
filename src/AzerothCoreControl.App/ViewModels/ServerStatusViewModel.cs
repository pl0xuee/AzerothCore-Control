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

    public bool IsRunning => State == ServerState.Running;

    partial void OnStateChanged(ServerState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusText));
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
