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

        // Global safety net: log unhandled UI/background exceptions and keep the app alive instead of
        // hard-crashing (e.g. from a tray-menu interaction). The message is also shown to the user.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unhandled UI exception");
            MessageBox.Show(ex.Exception.Message, "AzerothCore Control — error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Error(ex.ExceptionObject as Exception, "Unhandled domain exception");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unobserved task exception");
            ex.SetObserved();
        };

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

        // Start hidden in the tray only when launched at boot with --minimized (or explicitly configured);
        // a normal launch shows the window maximized.
        var startMinimized = e.Args.Contains("--minimized") || _coordinator.Settings.StartMinimizedToTray;

        var window = new MainWindow { DataContext = MainViewModel };
        MainWindow = window;
        if (!startMinimized)
        {
            window.WindowState = WindowState.Maximized;
            window.Show();
        }

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
