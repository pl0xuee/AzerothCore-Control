using System.Windows.Threading;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Live status of one supervised server, bound by a dashboard card.</summary>
public sealed partial class ServerStatusViewModel : ObservableObject
{
    private readonly ServerProcessSupervisor _supervisor;
    private readonly ServerCoordinator _coordinator;
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

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _actionMessage = "";

    /// <summary>The port this server listens on, read from its .conf — the first thing you check when clients can't connect.</summary>
    [NotifyPropertyChangedFor(nameof(EndpointText))]
    [ObservableProperty] private int? _listenPort;

    /// <summary>OS process id — for Task Manager, netstat, or a crash dump.</summary>
    [NotifyPropertyChangedFor(nameof(PidText))]
    [ObservableProperty] private int? _processId;

    /// <summary>The last thing the supervisor reported (started, crashed + why, restarting).</summary>
    [ObservableProperty] private string _lastEvent = "";

    /// <summary>Who's in the world. Null when it can't be read (MySQL down, not configured, server stopped).</summary>
    [NotifyPropertyChangedFor(nameof(PlayersText))]
    [NotifyPropertyChangedFor(nameof(BotsText))]
    [ObservableProperty] private WorldPopulation? _population;

    /// <summary>Only the world server has a population — authserver has no characters in it.</summary>
    public bool ShowsPopulation => Kind == ServerKind.World;

    public string PlayersText => IsRunning && Population != null ? Population.Players.ToString() : "—";
    public string BotsText => IsRunning && Population != null ? Population.Bots.ToString() : "—";

    public ServerStatusViewModel(ServerProcessSupervisor supervisor, ServerCoordinator coordinator)
    {
        _supervisor = supervisor;
        _coordinator = coordinator;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _state = supervisor.State;

        supervisor.StateChanged += (_, s) => _dispatcher.BeginInvoke(() =>
        {
            State = s;
            RestartCount = supervisor.RestartCount;
            ProcessId = supervisor.ProcessId;
            StartServerCommand.NotifyCanExecuteChanged();
            StopServerCommand.NotifyCanExecuteChanged();
        });

        // The crash diagnostic ("Crashed — FATAL: cannot connect to database") is the most useful line this
        // app produces; it belonged in the console only, where it scrolls away.
        supervisor.Notable += e => _dispatcher.BeginInvoke(() => LastEvent = e.Message);

        RefreshConfigFacts();
    }

    private DateTimeOffset _lastConfigRead;
    private DateTimeOffset _lastPopulationRead;
    private bool _populationQueryInFlight;

    /// <summary>
    /// Refresh the player/bot counts. Called from the dashboard's per-second tick but throttled to 5s, and
    /// never overlapped: it's a round-trip to MySQL, and a slow server must not queue up queries behind it.
    /// </summary>
    public void RefreshPopulation()
    {
        if (!ShowsPopulation)
            return;

        if (!IsRunning)
        {
            Population = null;   // nothing is in a world that isn't running
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_populationQueryInFlight || now - _lastPopulationRead < TimeSpan.FromSeconds(5))
            return;
        _lastPopulationRead = now;
        _populationQueryInFlight = true;

        // Off the UI thread: this opens a TCP connection and runs a query.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _coordinator.Population.QueryAsync().ConfigureAwait(false);
                _ = _dispatcher.BeginInvoke(() => Population = result);
            }
            catch
            {
                _ = _dispatcher.BeginInvoke(() => Population = null);
            }
            finally
            {
                _populationQueryInFlight = false;
            }
        });
    }

    /// <summary>
    /// Re-read the .conf-derived facts. Called from the dashboard's per-second tick, but throttled: the run
    /// directory can be set in Settings at any time (the first-run flow is "launch with nothing configured,
    /// then set it"), yet re-reading a .conf every second would be pointless disk traffic.
    /// </summary>
    public void RefreshConfigFacts(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && _lastConfigRead != default && now - _lastConfigRead < TimeSpan.FromSeconds(15))
            return;
        _lastConfigRead = now;

        var s = _coordinator.Settings;
        ListenPort = AcoreConfigReader.FindListenPort(s.RunDirectory ?? s.DeployDirectory, Kind);
    }

    public string PidText => ProcessId is { } pid ? pid.ToString() : "—";

    /// <summary>e.g. "0.0.0.0:3724" — what a client actually connects to.</summary>
    public string EndpointText => ListenPort is { } port ? $"port {port}" : "—";

    private bool CanStart => !IsBusy && State is ServerState.Stopped or ServerState.Crashed;
    private bool CanStop => !IsBusy && State is ServerState.Running or ServerState.Restarting;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartServerAsync()
    {
        IsBusy = true;
        ActionMessage = "Starting…";
        try
        {
            await _coordinator.StartServerAsync(Kind).ConfigureAwait(true);
            ActionMessage = "";
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
        }
        finally { IsBusy = false; RefreshCommands(); }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopServerAsync()
    {
        IsBusy = true;
        // For the world server this is a SAFE shutdown (in-game warning + save) — may take a few seconds.
        ActionMessage = Kind == ServerKind.World ? "Saving & shutting down…" : "Stopping…";
        try
        {
            await _coordinator.StopServerAsync(Kind).ConfigureAwait(true);
            ActionMessage = "";
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
        }
        finally { IsBusy = false; RefreshCommands(); }
    }

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    private void RefreshCommands()
    {
        StartServerCommand.NotifyCanExecuteChanged();
        StopServerCommand.NotifyCanExecuteChanged();
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
        // These read IsRunning too — without this the last known counts would linger on a stopped server.
        OnPropertyChanged(nameof(PlayersText));
        OnPropertyChanged(nameof(BotsText));
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
