using System.Diagnostics;
using AzerothCoreControl.Core.Services;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Dedicated "Updates" area for updating the control app itself from its GitHub Releases.</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _statusMessage = "Click “Check for updates” to see if a newer version is available.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppUpdate))]
    [NotifyPropertyChangedFor(nameof(LatestVersionText))]
    [NotifyPropertyChangedFor(nameof(ReleaseNotes))]
    private ReleaseInfo? _availableAppUpdate;

    public UpdatesViewModel(ServerCoordinator coordinator) => _coordinator = coordinator;

    public string CurrentVersion => GitHubReleaseService.CurrentAppVersion;
    public string RepositoryName => _coordinator.Settings.GitHub.AppReleaseRepo ?? "(not configured)";
    public bool HasAppUpdate => AvailableAppUpdate != null;
    public string LatestVersionText => AvailableAppUpdate?.TagName ?? "—";
    public string? ReleaseNotes => AvailableAppUpdate?.Body;

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        StatusMessage = "Checking GitHub for a new release…";
        try
        {
            var release = await _coordinator.Releases.CheckForAppUpdateAsync().ConfigureAwait(true);
            AvailableAppUpdate = release;
            StatusMessage = release == null
                ? $"You're on the latest version ({CurrentVersion})."
                : $"Update available: {release.Name} — published {release.PublishedAt:yyyy-MM-dd}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Update check failed: " + ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Open the release page (the packaged .exe asset is attached there).</summary>
    [RelayCommand]
    private void DownloadUpdate()
    {
        if (AvailableAppUpdate == null) return;
        Process.Start(new ProcessStartInfo(AvailableAppUpdate.HtmlUrl) { UseShellExecute = true });
    }
}
