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

    [NotifyPropertyChangedFor(nameof(CanRunBatch))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllAndBuildCommand))]
    [ObservableProperty] private bool _isChecking;

    [ObservableProperty] private string _status = "Not checked yet.";
    [ObservableProperty] private ModuleRowViewModel? _selectedModule;

    // Both commands walk every module, so neither may run while the other is — a re-check rebuilds the very
    // rows the batch is updating, and a batch pulls the trees the check is reading.
    [NotifyPropertyChangedFor(nameof(CanRunBatch))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllAndBuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckModulesCommand))]
    [ObservableProperty] private bool _isUpdatingAll;

    /// <summary>
    /// Live step-by-step text for the batch, kept apart from <see cref="Status"/> so the check summary
    /// survives. Null until a batch has run — the line is hidden rather than blank.
    /// </summary>
    [ObservableProperty] private string? _batchStatus;

    [ObservableProperty] private bool _batchFailed;

    /// <summary>
    /// Replace modules whose pull is refused with the remote's latest, instead of leaving them behind.
    /// </summary>
    /// <remarks>
    /// Off by default and deliberately not remembered between runs. It discards local edits and commits (to a
    /// backup folder), so it should be a decision the user makes for a specific run — not a setting that
    /// quietly stays on and eats their work three weeks later.
    /// </remarks>
    [ObservableProperty] private bool _replaceUnpullable;

    public ObservableCollection<ModuleRowViewModel> Modules { get; } = new();

    /// <summary>Compiler output from the last failed "Update all" — one build covers every module.</summary>
    public BuildReportViewModel BatchReport { get; }

    public bool CanRunBatch => !IsUpdatingAll && !IsChecking;

    public ModulesViewModel(ServerCoordinator coordinator)
    {
        _coordinator = coordinator;
        BatchReport = new BuildReportViewModel(() => "All modules");
    }

    /// <summary>Re-scan all modules against their GitHub remotes.</summary>
    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task CheckModulesAsync()
    {
        if (IsChecking || IsUpdatingAll) return;
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

    /// <summary>
    /// Pull every git-backed module, then compile and deploy ONCE.
    /// </summary>
    /// <remarks>
    /// The per-row "Pull + Build" is fine for one module but wrong to repeat: each run takes the servers down,
    /// backs up the databases and recompiles the whole tree, so updating twenty modules that way means twenty
    /// full rebuilds of code that is compiled into a single target anyway. This does the pulls first and pays
    /// for the build once — which also means the cmake review window (on by default) opens once, not per
    /// module.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task UpdateAllAndBuildAsync()
    {
        if (IsUpdatingAll || IsChecking) return;

        // "Update all" over a stale list would silently skip modules found since the last check.
        if (Modules.Count == 0)
        {
            await CheckModulesAsync().ConfigureAwait(true);
            if (Modules.Count == 0)
                return;
        }

        // A ZIP-installed folder has no remote to pull from; it would only contribute a failure row.
        var updatable = Modules.Where(m => m.Model.IsGitRepo).ToList();
        var skipped = Modules.Count - updatable.Count;
        if (updatable.Count == 0)
        {
            BatchStatus = "No git-backed modules to update — use Re-clone to convert a ZIP install first.";
            BatchFailed = true;
            return;
        }

        // The destructive option gets its own sentence and its own warning icon — it must never read as a
        // detail of an otherwise routine update.
        var stuckPolicy = ReplaceUnpullable
            ? "Any module that WON'T pull (local edits, diverged history) will be REPLACED with the latest " +
              "from its remote. Its current folder is moved to module-backups/ — your local edits and commits " +
              "will only exist there afterwards.\n\n"
            : "A module with local edits is left at its current commit rather than aborting the run.\n\n";

        var answer = System.Windows.MessageBox.Show(
            $"Pull {updatable.Count} modules, then rebuild and deploy?\n\n" +
            (skipped > 0 ? $"{skipped} ZIP-installed module(s) will be skipped — they have no remote to pull from.\n\n" : "") +
            "Running servers will be shut down gracefully and restarted afterwards.\n\n" +
            stuckPolicy +
            "The rebuild can take a long while.",
            ReplaceUnpullable ? "Update all modules, replacing stuck ones" : "Update all modules and build",
            System.Windows.MessageBoxButton.YesNo,
            ReplaceUnpullable ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        IsUpdatingAll = true;
        BatchFailed = false;
        BatchStatus = "Starting…";
        BatchReport.Clear();

        // The rows' own buttons must not stay live while the batch is pulling those same trees.
        foreach (var row in updatable)
        {
            row.IsBusy = true;
            row.Report.Clear();
        }

        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                BatchStatus = p.Message;
                if (p.Step == UpdateStep.Build)
                    BatchReport.Capture(p.Message);
            });

            var paths = updatable.Select(m => m.Model.Path).ToList();
            var report = await _coordinator.Orchestrator
                .RunAsync(paths, rebuild: true, progress, replaceUnpullable: ReplaceUnpullable)
                .ConfigureAwait(true);

            // A ZIP-installed module is excluded from the pulls but STILL COMPILED — so it can break the build
            // while never appearing in the report. Saying so only in the pre-run dialog leaves the user
            // staring at errors from a module the result never mentions.
            var skippedNote = skipped == 0
                ? ""
                : $" {skipped} module(s) without a git checkout were not updated, but are still compiled — " +
                  "re-clone them if the errors are theirs.";

            BatchStatus = report.Message + skippedNote;
            BatchFailed = !report.Success;
            if (!report.Success)
                BatchReport.Fail(report.Message + skippedNote);

            // Show each module its own pull outcome, so the grid's "Last result" column stops describing
            // whatever the user last did to that row by hand.
            var byName = updatable.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var pull in report.Pulls)
            {
                if (!byName.TryGetValue(pull.Name, out var row))
                    continue;
                row.ActionResult = pull.Message;
                row.ActionFailed = !pull.Success;
            }
        }
        catch (Exception ex)
        {
            BatchStatus = "Failed: " + ex.Message;
            BatchFailed = true;
            BatchReport.Fail(ex.Message);
        }
        finally
        {
            foreach (var row in updatable)
                row.IsBusy = false;
            IsUpdatingAll = false;
        }

        // The pulls moved every module's commit — the grid would otherwise keep advertising updates that are
        // already applied. Deliberately outside the finally: IsUpdatingAll must be clear before this runs.
        await CheckModulesAsync().ConfigureAwait(true);
    }
}

/// <summary>One row in the modules table.</summary>
public sealed partial class ModuleRowViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;

    /// <summary>Compiler output from the last failed build that involved this module.</summary>
    public BuildReportViewModel Report { get; }

    // Model IS reassigned (after a re-clone re-checks the row), so every computed property over it must be
    // notified — otherwise the row would keep showing "not a git repository" for a folder that is now a
    // proper checkout.
    [NotifyPropertyChangedFor(nameof(Name))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(CanPull))]
    [NotifyPropertyChangedFor(nameof(IsRecloneable))]
    [NotifyPropertyChangedFor(nameof(CanReclone))]
    [NotifyPropertyChangedFor(nameof(HasRemoteMismatch))]
    [NotifyPropertyChangedFor(nameof(CanSwitchRemote))]
    [NotifyPropertyChangedFor(nameof(MismatchText))]
    [NotifyPropertyChangedFor(nameof(SwitchButtonText))]
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
    [NotifyPropertyChangedFor(nameof(CanSwitchRemote))]
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _actionResult;

    /// <summary>Drives the colour of <see cref="ActionResult"/> — a failed build shouldn't read as muted chatter.</summary>
    [ObservableProperty] private bool _actionFailed;

    public ModuleRowViewModel(ModuleStatus model, ServerCoordinator coordinator)
    {
        _model = model;
        _coordinator = coordinator;
        Report = new BuildReportViewModel(() => Name);
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

    /// <summary>This checkout's origin disagrees with the repo it's pinned to in settings.</summary>
    public bool HasRemoteMismatch => Model.HasRemoteMismatch;

    public bool CanSwitchRemote => HasRemoteMismatch && !IsBusy;

    /// <summary>
    /// Spells the disagreement out both ways. "Pinned to X" alone reads as settled fact; the point is that
    /// updates are currently being checked against something else.
    /// </summary>
    /// <remarks>
    /// A built-in suggestion is worded as ours, not theirs. Telling someone they "pinned" a repo they never
    /// chose would be a lie, and would send them hunting through settings for an entry that isn't there.
    /// </remarks>
    public string MismatchText => !HasRemoteMismatch
        ? ""
        : Model.PinnedByUser
            ? $"Pinned to {Model.PinnedRepo}, but this checkout follows {Model.GitHubRepo ?? "no remote"}."
            : $"This follows {Model.GitHubRepo ?? "no remote"}, which is no longer maintained. " +
              $"{Model.PinnedRepo} is the fork that carries it on.";

    /// <summary>Ours reads as a suggestion; theirs reads as the instruction it is.</summary>
    public string SwitchButtonText => Model.PinnedByUser ? "Use pinned repo" : "Use maintained fork";

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
    /// Point this checkout's origin at the repo it's pinned to, so checks and pulls follow the fork the user
    /// actually runs instead of whatever it was first cloned from.
    /// </summary>
    /// <remarks>
    /// Switching remotes usually reveals a diverged history — that is the normal state of a fork, not a
    /// failure. When it happens the only way across is to re-clone, so this offers exactly that as a second,
    /// separately-confirmed step rather than resetting the user's commits away on its own initiative.
    /// </remarks>
    [RelayCommand]
    private async Task SwitchToPinnedRepoAsync()
    {
        var cloneUrl = Model.PinnedCloneUrl;
        var pinned = Model.PinnedRepo;
        if (cloneUrl == null || pinned == null || !CanSwitchRemote)
            return;

        // Say whose idea this is. A suggestion the app made must not be presented as a choice the user
        // already made — they'd go looking for a setting they never created.
        var provenance = Model.PinnedByUser
            ? $"It currently follows {Model.GitHubRepo ?? "no remote"}, but you've pinned it to {pinned}."
            : $"It currently follows {Model.GitHubRepo ?? "no remote"}, which is no longer maintained. " +
              $"{pinned} is the fork that carries it on — this is a suggestion from the app, not a pin you set.";

        var answer = System.Windows.MessageBox.Show(
            $"Point {Name} at {pinned}?\n\n" +
            provenance + " This rewrites the module's origin remote " +
            "and fetches — no files are changed and nothing is merged, so your working tree is untouched.\n\n" +
            "If the two histories have diverged (usual for a fork), you'll be told what it would take to move across.",
            "Switch remote",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        IsBusy = true;
        ActionFailed = false;
        ActionResult = "Switching remote…";
        try
        {
            // Fetching is a network operation — keep it off the UI thread.
            var result = await Task.Run(() => _coordinator.ModuleUpdater.RepointRemote(Model.Path, cloneUrl)).ConfigureAwait(true);
            ActionResult = result.Message;
            ActionFailed = !result.Success;

            if (result.Success)
                await RecheckAsync().ConfigureAwait(true);

            // Only a diverged history needs the destructive follow-up, and only the user can authorise it.
            if (result.Diverged)
                await OfferRecloneOntoPinnedRepoAsync(pinned, cloneUrl).ConfigureAwait(true);
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

    /// <summary>The re-clone that a diverged fork needs, asked for on its own terms.</summary>
    private async Task OfferRecloneOntoPinnedRepoAsync(string pinned, string cloneUrl)
    {
        var answer = System.Windows.MessageBox.Show(
            $"{Name} and {pinned} have diverged, so no pull can move you onto it.\n\n" +
            $"Re-clone {Name} from {pinned}?\n\n" +
            $"Your current folder is moved aside (kept as a backup), not deleted — but any local commits or " +
            "edits will only exist there afterwards. Rebuild once it's done.",
            "Re-clone onto pinned repo",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        ActionResult = "Cloning…";
        var result = await Task.Run(() =>
            _coordinator.ModuleUpdater.Reclone(Model.Path, cloneUrl, replaceGitRepo: true)).ConfigureAwait(true);
        ActionResult = result.Message;
        ActionFailed = !result.Success;
        if (result.Success)
            await RecheckAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Re-read this module after its remote or checkout changed, so the row stops describing the old state.
    /// </summary>
    /// <remarks>
    /// Task.Run because the git work (RetrieveStatus over every file) runs synchronously before the first
    /// await, which would freeze the window right after an operation we were careful to keep off the UI thread.
    /// </remarks>
    private async Task RecheckAsync()
    {
        try
        {
            var path = Model.Path;
            Model = await Task.Run(() => _coordinator.ModuleChecker.CheckOneAsync(path)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The operation itself worked; failing to re-read it is cosmetic — a re-check will pick it up.
            Serilog.Log.Warning(ex, "Re-check failed for {Module}", Name);
        }
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

            // It's a real checkout now — re-read it so the row stops calling itself a ZIP install.
            if (result.Success)
                await RecheckAsync().ConfigureAwait(true);
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
        Report.Clear();

        try
        {
            // Progress messages are steps, not verdicts — only the final report decides success.
            var progress = new Progress<UpdateProgress>(p =>
            {
                ActionResult = p.Message;
                if (p.Step == UpdateStep.Build)
                    Report.Capture(p.Message);
            });
            var report = await _coordinator.Orchestrator.RunAsync(Model.Path, rebuild: true, progress).ConfigureAwait(true);
            ActionResult = report.Message;
            ActionFailed = !report.Success;
            if (!report.Success)
                Report.Fail(report.Message);
        }
        catch (Exception ex)
        {
            ActionResult = "Failed: " + ex.Message;
            ActionFailed = true;
            Report.Fail(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Carry a previous row's outcome onto this one, so re-checking for updates doesn't discard the
    /// explanation of a build that failed minutes ago.
    /// </summary>
    /// <remarks>
    /// The last result is carried even with no build report attached: an "Update all" finishes by re-checking
    /// every module, and without this the per-module pull messages it just wrote would be wiped by the very
    /// refresh meant to show their effect.
    /// </remarks>
    public void AdoptBuildReport(ModuleRowViewModel old)
    {
        Report.Adopt(old.Report);
        if (old.ActionResult == null)
            return;
        ActionResult = old.ActionResult;
        ActionFailed = old.ActionFailed;
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
