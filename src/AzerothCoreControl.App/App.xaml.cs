using System.IO;
using System.Windows;
using System.Windows.Threading;
using AzerothCoreControl.App.Services;
using AzerothCoreControl.App.ViewModels;
using AzerothCoreControl.Core.Services;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;
using Serilog.Extensions.Logging;

namespace AzerothCoreControl.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "AzerothCoreControl.SingleInstance.v1";
    private const string ShowEventName = "AzerothCoreControl.ShowWindow.v1";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showEventRegistration;
    private ServerCoordinator? _coordinator;
    private TaskbarIcon? _trayIcon;
    private string _crashLogPath = "";

    public ServerCoordinator Coordinator => _coordinator!;
    public MainViewModel MainViewModel { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance is already running — ask it to come to the foreground, then exit.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { /* best effort */ }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // A tray app must not exit just because its window was closed/hidden — only via explicit Quit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzerothCoreControl");
        Directory.CreateDirectory(appDataDir);
        _crashLogPath = Path.Combine(appDataDir, "last-crash.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(appDataDir, "logs", "app-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();
        var loggerFactory = new SerilogLoggerFactory(Log.Logger);

        // Global safety net: log the FULL exception, write it to last-crash.txt, and show it — instead of
        // hard-crashing. This is also how we surface the actual error behind hard-to-reproduce crashes.
        DispatcherUnhandledException += (_, ex) =>
        {
            ReportError(ex.Exception, "UI");
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            ReportError(ex.ExceptionObject as Exception, "domain");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            ReportError(ex.Exception, "task");
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

        var window = new MainWindow { DataContext = MainViewModel };
        MainWindow = window;

        // Listen for "show window" signals from second launches, and bring ourselves to the foreground.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showEventRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => Dispatcher.BeginInvoke(BringToForeground), null, -1, executeOnlyOnce: false);

        var startMinimized = e.Args.Contains("--minimized") || _coordinator.Settings.StartMinimizedToTray;
        if (!startMinimized)
            BringToForeground();

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

    /// <summary>Show, restore, and focus the main window. Safe to call repeatedly and on any thread's marshal.</summary>
    public void BringToForeground()
    {
        try
        {
            if (MainWindow is not { } window)
                return;
            if (!window.IsVisible)
            {
                window.WindowState = WindowState.Maximized;
                window.Show();
            }
            else if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Maximized;
            }
            window.Activate();
            // Brief topmost flip reliably pulls the window in front of other apps.
            window.Topmost = true;
            window.Topmost = false;
        }
        catch (Exception ex)
        {
            ReportError(ex, "show-window");
        }
    }

    private void ReportError(Exception? ex, string source)
    {
        var text = ex?.ToString() ?? "(unknown error)";
        try { Log.Error(ex, "Unhandled {Source} exception", source); } catch { }
        try { File.WriteAllText(_crashLogPath, $"[{source}] {DateTimeOffset.Now:O}\n{text}"); } catch { }
        try
        {
            MessageBox.Show(text.Length > 2000 ? text[..2000] + "…" : text,
                "AzerothCore Control — error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* nothing more we can do */ }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _showEventRegistration?.Unregister(null);
        _showEvent?.Dispose();
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
