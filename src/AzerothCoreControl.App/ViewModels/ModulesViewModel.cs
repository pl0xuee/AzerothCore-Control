using System.Collections.ObjectModel;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Modules tab: lists installed modules with GitHub update status and pull/build actions.</summary>
public sealed partial class ModulesViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _status = "Not checked yet.";
    [ObservableProperty] private ModuleRowViewModel? _selectedModule;

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

}

/// <summary>One row in the modules table.</summary>
public sealed partial class ModuleRowViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;

    [ObservableProperty] private ModuleStatus _model;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _actionResult;

    /// <summary>Drives the colour of <see cref="ActionResult"/> — a failed build shouldn't read as muted chatter.</summary>
    [ObservableProperty] private bool _actionFailed;

    public ModuleRowViewModel(ModuleStatus model, ServerCoordinator coordinator)
    {
        _model = model;
        _coordinator = coordinator;
    }

    public string Name => Model.Name;
    public string StatusText => Model.Error != null ? $"Error: {Model.Error}"
        : Model.UpdateAvailable ? $"{Model.BehindBy} behind"
        : "Up to date";
    public bool HasError => Model.Error != null;
    public bool CanPull => Model.CanFastForward && !IsBusy;

    public IReadOnlyList<ModuleCommit> IncomingCommits => Model.IncomingCommits;
    public bool HasIncomingCommits => Model.IncomingCommits.Count > 0;

    /// <summary>Human-readable "what changed" list of the incoming commits.</summary>
    public string ChangesText => HasIncomingCommits
        ? string.Join(Environment.NewLine,
            IncomingCommits.Select(c => $"{c.ShortSha}  {c.Summary}  — {c.Author}, {c.Date:yyyy-MM-dd}"))
        : Model.UpdateAvailable ? "(commit details unavailable)" : "Up to date — nothing to pull.";

    [RelayCommand]
    private void Pull()
    {
        RunAction(() =>
        {
            var result = _coordinator.ModuleUpdater.Pull(Model.Path);
            return (result.Success, result.Message);
        });
    }

    [RelayCommand]
    private async Task PullAndBuildAsync()
    {
        IsBusy = true;
        ActionFailed = false;
        ActionResult = "Updating…";
        try
        {
            // Progress messages are steps, not verdicts — only the final report decides success.
            var progress = new Progress<UpdateProgress>(p => ActionResult = p.Message);
            var report = await _coordinator.Orchestrator.RunAsync(Model.Path, rebuild: true, progress).ConfigureAwait(true);
            ActionResult = report.Message;
            ActionFailed = !report.Success;
        }
        catch (Exception ex)
        {
            ActionResult = "Failed: " + ex.Message;
            ActionFailed = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RunAction(Func<(bool Success, string Message)> action)
    {
        IsBusy = true;
        ActionFailed = false;
        try
        {
            var (success, message) = action();
            ActionResult = message;
            ActionFailed = !success;
        }
        catch (Exception ex)
        {
            ActionResult = "Failed: " + ex.Message;
            ActionFailed = true;
        }
        finally { IsBusy = false; }
    }
}
