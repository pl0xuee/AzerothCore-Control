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

            // Re-checking rebuilds every row, which would throw away a build report the user hasn't read yet.
            var previous = Modules.ToDictionary(r => r.Model.Path, StringComparer.OrdinalIgnoreCase);
            Modules.Clear();
            foreach (var m in results)
            {
                var row = new ModuleRowViewModel(m, _coordinator);
                if (previous.TryGetValue(m.Path, out var old))
                    row.AdoptBuildReport(old);
                Modules.Add(row);
            }
            var updates = results.Count(r => r.UpdateAvailable);
            var failed = results.Count(r => r.Error != null);
            var checkable = results.Count - failed;

            Status = results.Count == 0
                ? $"No modules found in {_coordinator.ModuleChecker.ModulesFolder}."
                : updates == 0
                    ? $"All {checkable} modules up to date."
                    : $"{updates} of {checkable} modules have updates available.";
            // Don't let unchecked modules hide behind an "all up to date" — they're listed with a reason.
            if (failed > 0)
                Status += $" {failed} could not be checked (see the Status column).";
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

    /// <summary>
    /// How much build output to retain for the failure report. A failing AzerothCore build emits far more
    /// than this, but the diagnostics are extracted as lines arrive, so the cap only bounds the raw tail.
    /// </summary>
    private const int MaxLogLines = 400;

    [ObservableProperty] private ModuleStatus _model;

    // CanPull is a computed getter over IsBusy; without this the Pull button's IsEnabled never re-evaluates
    // and stays clickable throughout a multi-minute build, letting a git pull run against the source tree
    // the compiler is reading.
    [NotifyPropertyChangedFor(nameof(CanPull))]
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _actionResult;

    /// <summary>Drives the colour of <see cref="ActionResult"/> — a failed build shouldn't read as muted chatter.</summary>
    [ObservableProperty] private bool _actionFailed;

    [ObservableProperty] private string _buildReportSummary = "";
    [ObservableProperty] private bool _hasBuildReport;

    // Which list the report shows: the extracted diagnostics, or the raw tail when none were recognised.
    [ObservableProperty] private bool _showBuildErrors;
    [ObservableProperty] private bool _showRawBuildLog;

    /// <summary>Compiler diagnostics from the last failed build, newest run only.</summary>
    public ObservableCollection<string> BuildErrors { get; } = new();

    /// <summary>Tail of the raw build output, kept so a failure with no recognised diagnostic still shows something.</summary>
    public ObservableCollection<string> BuildLog { get; } = new();

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
        ClearBuildReport();

        // Every build line, kept only for the duration of this run so the report reflects one build.
        var captured = new List<string>();
        try
        {
            // Progress messages are steps, not verdicts — only the final report decides success.
            var progress = new Progress<UpdateProgress>(p =>
            {
                ActionResult = p.Message;
                if (p.Step == UpdateStep.Build)
                    Capture(captured, p.Message);
            });
            var report = await _coordinator.Orchestrator.RunAsync(Model.Path, rebuild: true, progress).ConfigureAwait(true);
            ActionResult = report.Message;
            ActionFailed = !report.Success;
            if (!report.Success)
                BuildFailureReport(report.Message, captured);
        }
        catch (Exception ex)
        {
            ActionResult = "Failed: " + ex.Message;
            ActionFailed = true;
            BuildFailureReport(ex.Message, captured);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Capture(List<string> captured, string line)
    {
        captured.Add(line);
        BuildLog.Add(line);
        while (BuildLog.Count > MaxLogLines)
            BuildLog.RemoveAt(0);
    }

    /// <summary>Turn a failed run's captured output into the report shown under the modules table.</summary>
    private void BuildFailureReport(string message, List<string> captured)
    {
        BuildErrors.Clear();
        foreach (var error in BuildDiagnostics.ExtractErrors(captured))
            BuildErrors.Add(error);

        BuildReportSummary = BuildErrors.Count switch
        {
            0 => $"{message} No compiler diagnostics recognised — showing the raw output.",
            1 => $"{message} 1 error:",
            var n => $"{message} {n} errors:",
        };
        ShowBuildErrors = BuildErrors.Count > 0;
        ShowRawBuildLog = BuildErrors.Count == 0;
        HasBuildReport = true;
    }

    /// <summary>
    /// Carry a previous row's build report onto this one, so re-checking for updates doesn't discard the
    /// explanation of a build that failed minutes ago.
    /// </summary>
    public void AdoptBuildReport(ModuleRowViewModel old)
    {
        if (!old.HasBuildReport)
            return;

        foreach (var e in old.BuildErrors) BuildErrors.Add(e);
        foreach (var l in old.BuildLog) BuildLog.Add(l);
        BuildReportSummary = old.BuildReportSummary;
        ShowBuildErrors = old.ShowBuildErrors;
        ShowRawBuildLog = old.ShowRawBuildLog;
        HasBuildReport = true;
        ActionResult = old.ActionResult;
        ActionFailed = old.ActionFailed;
    }

    private void ClearBuildReport()
    {
        HasBuildReport = false;
        ShowBuildErrors = false;
        ShowRawBuildLog = false;
        BuildReportSummary = "";
        BuildErrors.Clear();
        BuildLog.Clear();
    }

    /// <summary>Copy the whole report so it can be pasted into an issue or a Discord thread.</summary>
    [RelayCommand]
    private void CopyBuildReport()
    {
        var text = string.Join(Environment.NewLine,
            new[] { $"{Name}: {ActionResult}", "" }
                .Concat(BuildErrors)
                .Concat(new[] { "", "--- build output (last " + BuildLog.Count + " lines) ---" })
                .Concat(BuildLog));
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* clipboard can be locked by another app — not worth failing the UI over */ }
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
