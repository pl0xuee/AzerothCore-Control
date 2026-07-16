using System.IO;
using System.Windows;
using AzerothCoreControl.App.Services;
using AzerothCoreControl.App.ViewModels;
using AzerothCoreControl.Core.Services;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace AzerothCoreControl.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "AzerothCoreControl.SingleInstance.v1";
    private Mutex? _singleInstance;
    private ServerCoordinator? _coordinator;
    private TaskbarIcon? _trayIcon;

    public ServerCoordinator Coordinator => _coordinator!;
    public MainViewModel MainViewModel { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard — a second launch just exits (the supervisor keeps running).
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzerothCoreControl");
        Directory.CreateDirectory(appDataDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(appDataDir, "logs", "app-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();
        var loggerFactory = new SerilogLoggerFactory(Log.Logger);

        var store = new SettingsStore(Path.Combine(appDataDir, "settings.json"));
        _coordinator = new ServerCoordinator(store, loggerFactory);

        // Route Core's toast requests to native Windows toasts.
        _coordinator.Notifications.ToastRequested += (title, message, severity) =>
        {
            if (OperatingSystem.IsWindows())
                ToastNotifier.Show(title, message, severity);
        };

        MainViewModel = new MainViewModel(_coordinator);

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.DataContext = MainViewModel;

        var startMinimized = e.Args.Contains("--minimized") || _coordinator.Settings.StartMinimizedToTray;

        var window = new MainWindow { DataContext = MainViewModel };
        MainWindow = window;
        if (!startMinimized)
            window.Show();

        if (_coordinator.Settings.AutoStartServers)
            _ = _coordinator.StartAllAsync();

        // Automatic app self-update: when an update is staged, exit so the swap batch can replace the exe.
        _coordinator.AppUpdater.RestartRequired += () => Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow mw) mw.CloseForReal();
            Shutdown();
        });
        _coordinator.AppUpdater.UpdateAvailable += release => Dispatcher.BeginInvoke(() =>
            MainViewModel.Updates.AvailableAppUpdate = release);
        _coordinator.AppUpdater.StartBackgroundChecks();
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow == null)
            return;
        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void TrayQuit_Click(object sender, RoutedEventArgs e) => Shutdown();

    protected override async void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        if (_coordinator != null)
        {
            _coordinator.AppUpdater.Stop();
            await _coordinator.DisposeAsync();
        }
        await Log.CloseAndFlushAsync();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
