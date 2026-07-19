using System.Collections.ObjectModel;
using AzerothCoreControl.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>
/// The "why did that build fail" panel: extracted compiler diagnostics, a tail of the raw output as a
/// fallback, and a clipboard export.
/// </summary>
/// <remarks>
/// Shared by a single module's row and by the "Update all" batch, which need exactly the same widget — a
/// batch build fails in precisely the same ways a single one does, and its output is the more important of
/// the two to be able to read, since it covers every module at once.
/// </remarks>
public sealed partial class BuildReportViewModel : ObservableObject
{
    /// <summary>
    /// How much build output to retain. A failing AzerothCore build emits far more than this, but the
    /// diagnostics are extracted as lines arrive, so the cap only bounds the raw tail.
    /// </summary>
    private const int MaxLogLines = 400;

    /// <summary>Names the report in the clipboard export — a module name, or "All modules".</summary>
    private readonly Func<string> _title;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasReport;

    // Which list the report shows: the extracted diagnostics, or the raw tail when none were recognised.
    [ObservableProperty] private bool _showErrors;
    [ObservableProperty] private bool _showRawLog;

    /// <summary>Compiler diagnostics from the last failed build, newest run only.</summary>
    public ObservableCollection<string> Errors { get; } = new();

    /// <summary>Tail of the raw build output, kept so a failure with no recognised diagnostic still shows something.</summary>
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Every line of the current run, uncapped, so extraction sees output that has scrolled off <see cref="Log"/>.</summary>
    private readonly List<string> _captured = new();

    public BuildReportViewModel(Func<string> title) => _title = title;

    /// <summary>Start a fresh run: the report must describe one build, not an accumulation of them.</summary>
    public void Clear()
    {
        HasReport = false;
        ShowErrors = false;
        ShowRawLog = false;
        Summary = "";
        Errors.Clear();
        Log.Clear();
        _captured.Clear();
    }

    /// <summary>Record one line of build output.</summary>
    public void Capture(string line)
    {
        _captured.Add(line);
        Log.Add(line);
        while (Log.Count > MaxLogLines)
            Log.RemoveAt(0);
    }

    /// <summary>Turn the captured output into the report shown under the modules table.</summary>
    public void Fail(string message)
    {
        Errors.Clear();
        foreach (var error in BuildDiagnostics.ExtractErrors(_captured))
            Errors.Add(error);

        Summary = Errors.Count switch
        {
            0 => $"{message} No compiler diagnostics recognised — showing the raw output.",
            1 => $"{message} 1 error:",
            var n => $"{message} {n} errors:",
        };
        ShowErrors = Errors.Count > 0;
        ShowRawLog = Errors.Count == 0;
        HasReport = true;
    }

    /// <summary>
    /// Carry a previous report onto this one, so re-checking for updates doesn't discard the explanation of a
    /// build that failed minutes ago.
    /// </summary>
    public void Adopt(BuildReportViewModel old)
    {
        if (!old.HasReport)
            return;

        foreach (var e in old.Errors) Errors.Add(e);
        foreach (var l in old.Log) Log.Add(l);
        _captured.AddRange(old._captured);
        Summary = old.Summary;
        ShowErrors = old.ShowErrors;
        ShowRawLog = old.ShowRawLog;
        HasReport = true;
    }

    /// <summary>Copy the whole report so it can be pasted into an issue or a Discord thread.</summary>
    [RelayCommand]
    private void Copy()
    {
        var text = string.Join(Environment.NewLine,
            new[] { $"{_title()}: {Summary}", "" }
                .Concat(Errors)
                .Concat(new[] { "", $"--- build output (last {Log.Count} lines) ---" })
                .Concat(Log));
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* clipboard can be locked by another app — not worth failing the UI over */ }
    }
}
