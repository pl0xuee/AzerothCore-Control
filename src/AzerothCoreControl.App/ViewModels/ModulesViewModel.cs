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
            // Name the path that was checked: "no modules folder found" alone gives the user nothing to act on.
            var folder = _coordinator.ModuleChecker.FindModulesFolder();
            if (folder.Path == null)
            {
                Status = folder.Detail;
                return;
            }
            // Task.Run: CheckAllAsync opens each repo and walks its working tree synchronously between
            // awaits, which on a big install (mod-playerbots alone is thousands of files) would freeze the
            // window for the duration of the check.
            var results = await Task.Run(() => _coordinator.ModuleChecker.CheckAllAsync()).ConfigureAwait(true);

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
                ? $"The modules folder is empty: {folder.Path}"
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

    // Model IS reassigned (after a re-clone re-checks the row), so every computed property over it must be
    // notified — otherwise the row would keep showing "not a git repository" for a folder that is now a
    // proper checkout.
    [NotifyPropertyChangedFor(nameof(Name))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(CanPull))]
    [NotifyPropertyChangedFor(nameof(IsRecloneable))]
    [NotifyPropertyChangedFor(nameof(CanReclone))]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyPropertyChangedFor(nameof(RevisionText))]
    [NotifyPropertyChangedFor(nameof(IncomingCommits))]
    [NotifyPropertyChangedFor(nameof(HasIncomingCommits))]
    [NotifyPropertyChangedFor(nameof(ChangesText))]
    [ObservableProperty] private ModuleStatus _model;

    // CanPull/CanReclone are computed getters over IsBusy; without this their buttons' IsEnabled never
    // re-evaluates and they stay clickable throughout a multi-minute build, letting a git pull run against
    // the source tree the compiler is reading.
    [NotifyPropertyChangedFor(nameof(CanPull))]
    [NotifyPropertyChangedFor(nameof(CanReclone))]
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

    /// <summary>Status text plus, when we identified it from the catalogue, where it actually came from.</summary>
    public string? SourceText => Model.GitHubRepo;

    /// <summary>
    /// Branch and commits as one string ("master  a119a6d → 93aaea3"). These were three columns saying one
    /// thing, and the grid didn't have the width to spare.
    /// </summary>
    public string RevisionText
    {
        get
        {
            if (!Model.IsGitRepo)
                return "";
            var branch = string.IsNullOrEmpty(Model.Branch) ? "(detached)" : Model.Branch;
            var local = Model.LocalCommit ?? "?";
            // Only show the arrow when the remote is actually somewhere else.
            return Model.RemoteCommit == null || Model.RemoteCommit == local
                ? $"{branch}  {local}"
                : $"{branch}  {local} → {Model.RemoteCommit}";
        }
    }

    /// <summary>Re-clone is offered only for a non-git folder we could identify an upstream for.</summary>
    /// <remarks>Separate from <see cref="CanReclone"/> so the button greys out while cloning instead of
    /// disappearing out from under the click that started it.</remarks>
    public bool IsRecloneable => !Model.IsGitRepo && Model.CloneUrl != null;

    public bool CanReclone => IsRecloneable && !IsBusy;

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

    /// <summary>
    /// Replace a ZIP-installed folder with a real git clone so it becomes update-checkable. This moves the
    /// user's existing folder aside, so it asks first — and says exactly what it will do.
    /// </summary>
    [RelayCommand]
    private async Task RecloneAsync()
    {
        var cloneUrl = Model.CloneUrl;
        if (cloneUrl == null || !CanReclone)
            return;

        var answer = System.Windows.MessageBox.Show(
            $"Replace {Name} with a fresh git clone of {Model.GitHubRepo}?\n\n" +
            $"Your current folder will be moved aside (kept as {Name}.backup-<time>), not deleted, and the " +
            "latest code cloned in its place.\n\n" +
            "This brings in the newest upstream code, so rebuild afterwards. Any local edits you made will " +
            "be in the backup folder only.",
            "Re-clone module",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        IsBusy = true;
        ActionFailed = false;
        ActionResult = "Cloning…";
        try
        {
            // Cloning is a long network operation — keep it off the UI thread.
            var result = await Task.Run(() => _coordinator.ModuleUpdater.Reclone(Model.Path, cloneUrl)).ConfigureAwait(true);
            ActionResult = result.Message;
            ActionFailed = !result.Success;

            if (result.Success)
            {
                // It's a real checkout now — re-read it so the row stops calling itself a ZIP install.
                // Task.Run because CheckOneAsync does its git work (RetrieveStatus over every file in a fresh
                // clone) synchronously before its first await, which would freeze the window right after the
                // clone we were careful to keep off the UI thread.
                try
                {
                    var path = Model.Path;
                    Model = await Task.Run(() => _coordinator.ModuleChecker.CheckOneAsync(path)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    // The clone worked; failing to re-read it is cosmetic — a re-check will pick it up.
                    Serilog.Log.Warning(ex, "Re-check after re-clone failed for {Module}", Name);
                }
            }
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
