using System.IO;
using System.Windows.Threading;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Root view-model bound to MainWindow; owns the tab view-models and dashboard actions.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;
    private readonly DispatcherTimer _uptimeTimer;

    [ObservableProperty] private MySqlState _mySqlState;
    [ObservableProperty] private string _busyMessage = "";
    [ObservableProperty] private bool _isBusy;

    public ServerStatusViewModel World { get; }
    public ServerStatusViewModel Auth { get; }
    public ConsoleViewModel Console { get; }
    public ModulesViewModel Modules { get; }
    public UpdatesViewModel Updates { get; }
    public SettingsViewModel Settings { get; }
    public SchedulesViewModel Schedules { get; }

    public MainViewModel(ServerCoordinator coordinator)
    {
        _coordinator = coordinator;
        World = new ServerStatusViewModel(coordinator.World);
        Auth = new ServerStatusViewModel(coordinator.Auth);
        Console = new ConsoleViewModel(coordinator);
        Modules = new ModulesViewModel(coordinator);
        Updates = new UpdatesViewModel(coordinator);
        Settings = new SettingsViewModel(coordinator);
        Schedules = new SchedulesViewModel(coordinator);

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) =>
        {
            World.RefreshUptime();
            World.RefreshResources();
            Auth.RefreshUptime();
            Auth.RefreshResources();
            MySqlState = _coordinator.MySql.GetState();
        };
        _uptimeTimer.Start();
    }

    [RelayCommand]
    private async Task StartAllAsync() => await RunBusy("Starting servers…", () => _coordinator.StartAllAsync());

    [RelayCommand]
    private async Task StopAllAsync() => await RunBusy("Stopping servers…", () => _coordinator.StopAllAsync());

    [RelayCommand]
    private async Task RestartWorldAsync() => await RunBusy("Restarting world server…", async () =>
    {
        await _coordinator.World.StopAsync(graceful: true);
        var runDir = _coordinator.Settings.RunDirectory!;
        _coordinator.World.Start(Path.Combine(runDir, ServerKind.World.ExecutableName()), workingDirectory: runDir);
    });

    [RelayCommand]
    private async Task StartMySqlAsync()
        => await RunBusy("Starting MySQL…", () => _coordinator.MySql.EnsureRunningAsync(TimeSpan.FromSeconds(30)));

    private async Task RunBusy(string message, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyMessage = message;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            BusyMessage = "Error: " + ex.Message;
            await _coordinator.Notifications.NotifyAsync("AzerothCore Control", ex.Message, NotificationSeverity.Warning);
            return;
        }
        finally
        {
            IsBusy = false;
        }
        BusyMessage = "";
    }
}
