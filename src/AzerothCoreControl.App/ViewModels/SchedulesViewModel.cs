using System.Collections.ObjectModel;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Schedules &amp; backups tab: manage restart/backup jobs and run them on demand.</summary>
public sealed partial class SchedulesViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;

    [ObservableProperty] private string _newJobName = "Nightly restart";
    [ObservableProperty] private int _newJobHour = 4;
    [ObservableProperty] private int _newJobMinute = 0;
    [ObservableProperty] private ScheduledJobKind _newJobKind = ScheduledJobKind.Restart;
    [ObservableProperty] private string _backupResult = "";

    public ObservableCollection<ScheduledJob> Jobs { get; } = new();
    public ScheduledJobKind[] JobKinds { get; } = Enum.GetValues<ScheduledJobKind>();

    public SchedulesViewModel(ServerCoordinator coordinator)
    {
        _coordinator = coordinator;
        foreach (var job in coordinator.Settings.Schedules)
            Jobs.Add(job);
    }

    [RelayCommand]
    private void AddJob()
    {
        var job = new ScheduledJob
        {
            Name = NewJobName,
            Kind = NewJobKind,
            TimeOfDay = new TimeSpan(Math.Clamp(NewJobHour, 0, 23), Math.Clamp(NewJobMinute, 0, 59), 0),
        };
        Jobs.Add(job);
        _coordinator.Settings.Schedules.Add(job);
        _coordinator.SaveSettings();
    }

    [RelayCommand]
    private void RemoveJob(ScheduledJob? job)
    {
        if (job == null) return;
        Jobs.Remove(job);
        _coordinator.Settings.Schedules.RemoveAll(j => j.Id == job.Id);
        _coordinator.SaveSettings();
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        BackupResult = "Backing up…";
        var result = await _coordinator.Backup.BackupAsync(m => BackupResult = m).ConfigureAwait(true);
        BackupResult = result.Message;
    }

    [RelayCommand]
    private async Task RestartNowAsync()
    {
        await _coordinator.Schedule.RestartWithWarningsAsync().ConfigureAwait(true);
    }
}
