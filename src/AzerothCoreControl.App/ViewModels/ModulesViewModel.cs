using System.Collections.ObjectModel;
using System.Diagnostics;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Modules tab: lists installed modules with update status, plus the app self-update button.</summary>
public sealed partial class ModulesViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _status = "Not checked yet.";
    [ObservableProperty] private string? _appUpdateMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppUpdate))]
    private ReleaseInfo? _availableAppUpdate;

    public bool HasAppUpdate => AvailableAppUpdate != null;

    public ObservableCollection<ModuleRowViewModel> Modules { get; } = new();

    public ModulesViewModel(ServerCoordinator coordinator) => _coordinator = coordinator;

    /// <summary>Re-scan all modules against their GitHub remotes.</summary>
    [RelayCommand]
    private async Task CheckModulesAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        Status = "Checking modules…";
        try
        {
            if (_coordinator.ModuleChecker.ModulesFolder == null)
            {
                Status = "No modules folder found. Set the Source Directory in Settings.";
                return;
            }
            var results = await _coordinator.ModuleChecker.CheckAllAsync().ConfigureAwait(true);
            Modules.Clear();
            foreach (var m in results)
                Modules.Add(new ModuleRowViewModel(m, _coordinator));
            var updates = results.Count(r => r.UpdateAvailable);
            Status = updates == 0
                ? $"All {results.Count} modules up to date."
                : $"{updates} of {results.Count} modules have updates available.";
        }
        catch (Exception ex)
        {
            Status = "Check failed: " + ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>The "Update" button the user asked for — checks this app's GitHub releases.</summary>
    [RelayCommand]
    private async Task CheckForAppUpdateAsync()
    {
        AppUpdateMessage = "Checking for a new release…";
        try
        {
            var release = await _coordinator.Releases.CheckForAppUpdateAsync().ConfigureAwait(true);
            if (release == null)
            {
                AvailableAppUpdate = null;
                AppUpdateMessage = $"You're on the latest version ({GitHubReleaseService.CurrentAppVersion}).";
            }
            else
            {
                AvailableAppUpdate = release;
                AppUpdateMessage = $"Update available: {release.Name} (published {release.PublishedAt:yyyy-MM-dd}).";
            }
        }
        catch (Exception ex)
        {
            AppUpdateMessage = "Update check failed: " + ex.Message;
        }
    }

    /// <summary>Open the release page / download the new version's asset.</summary>
    [RelayCommand]
    private void DownloadAppUpdate()
    {
        if (AvailableAppUpdate == null) return;
        // Open the release page in the browser; the packaged .exe asset is attached there.
        Process.Start(new ProcessStartInfo(AvailableAppUpdate.HtmlUrl) { UseShellExecute = true });
    }
}

/// <summary>One row in the modules table.</summary>
public sealed partial class ModuleRowViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;

    [ObservableProperty] private ModuleStatus _model;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _actionResult;

    public ModuleRowViewModel(ModuleStatus model, ServerCoordinator coordinator)
    {
        _model = model;
        _coordinator = coordinator;
    }

    public string Name => Model.Name;
    public string StatusText => Model.Error != null ? $"Error: {Model.Error}"
        : Model.UpdateAvailable ? $"{Model.BehindBy} behind"
        : "Up to date";
    public bool CanPull => Model.CanFastForward && !IsBusy;

    [RelayCommand]
    private void Pull()
    {
        RunAction(() =>
        {
            var result = _coordinator.ModuleUpdater.Pull(Model.Path);
            return result.Message;
        });
    }

    [RelayCommand]
    private async Task PullAndBuildAsync()
    {
        IsBusy = true;
        ActionResult = "Updating…";
        try
        {
            var progress = new Progress<UpdateProgress>(p => ActionResult = p.Message);
            var report = await _coordinator.Orchestrator.RunAsync(Model.Path, rebuild: true, progress).ConfigureAwait(true);
            ActionResult = report.Message;
        }
        catch (Exception ex)
        {
            ActionResult = "Failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RunAction(Func<string> action)
    {
        IsBusy = true;
        try { ActionResult = action(); }
        catch (Exception ex) { ActionResult = "Failed: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
