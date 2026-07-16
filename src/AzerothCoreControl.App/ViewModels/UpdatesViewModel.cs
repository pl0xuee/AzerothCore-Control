using AzerothCoreControl.Core.Services;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Dedicated "Updates" area for updating the control app itself from its GitHub Releases.</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;
    private readonly AppUpdater _updater;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Click “Check for updates” to see if a newer version is available.";
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppUpdate))]
    [NotifyPropertyChangedFor(nameof(LatestVersionText))]
    [NotifyPropertyChangedFor(nameof(ReleaseNotes))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private ReleaseInfo? _availableAppUpdate;

    public UpdatesViewModel(ServerCoordinator coordinator)
    {
        _coordinator = coordinator;
        _updater = coordinator.AppUpdater;

        // These events can fire on a background thread (the auto-update loop), so marshal onto the UI
        // thread before touching bound state. UpdateAvailable is handled in App.xaml.cs (also marshaled),
        // which sets AvailableAppUpdate on this same VM — so we deliberately don't subscribe to it here.
        _updater.DownloadProgress += p => OnUi(() => DownloadProgress = p * 100);
        _updater.StageChanged += (_, msg) => OnUi(() => StatusMessage = msg);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public string CurrentVersion => GitHubReleaseService.CurrentAppVersion;
    public string RepositoryName => _coordinator.Settings.GitHub.AppReleaseRepo ?? "(not configured)";
    public bool HasAppUpdate => AvailableAppUpdate != null;
    public string LatestVersionText => AvailableAppUpdate?.TagName ?? "—";
    public string? ReleaseNotes => AvailableAppUpdate?.Body;

    /// <summary>Automatically check for updates on launch and periodically.</summary>
    public bool AutoCheck
    {
        get => _coordinator.Settings.AutoCheckForUpdates;
        set
        {
            if (_coordinator.Settings.AutoCheckForUpdates == value) return;
            _coordinator.Settings.AutoCheckForUpdates = value;
            _coordinator.SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>When an update is found, download and install it automatically (in-place, then relaunch).</summary>
    public bool AutoInstall
    {
        get => _coordinator.Settings.AutoInstallUpdates;
        set
        {
            if (_coordinator.Settings.AutoInstallUpdates == value) return;
            _coordinator.Settings.AutoInstallUpdates = value;
            _coordinator.SaveSettings();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Checking GitHub for a new release…";
        try
        {
            var release = await _updater.CheckAsync().ConfigureAwait(true);
            AvailableAppUpdate = release;
            StatusMessage = release == null
                ? $"You're on the latest version ({CurrentVersion})."
                : $"Update available: {release.Name} — published {release.PublishedAt:yyyy-MM-dd}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Update check failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanInstall => AvailableAppUpdate != null && !IsDownloading;

    /// <summary>Download the new build and swap it in place, then restart — not just a redownload.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallUpdateAsync()
    {
        if (AvailableAppUpdate == null) return;
        IsDownloading = true;
        InstallUpdateCommand.NotifyCanExecuteChanged();
        try
        {
            StatusMessage = "Downloading and installing update…";
            await _updater.DownloadAndApplyAsync(AvailableAppUpdate).ConfigureAwait(true);
            StatusMessage = "Update downloaded — the app will now restart to finish installing.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Install failed: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
    }
}
