using System.Diagnostics;
using System.IO;
using AzerothCoreControl.Core.Services;

namespace AzerothCoreControl.App.Services;

public enum AppUpdateStage { Idle, Checking, Downloading, Installing, Failed, UpToDate }

/// <summary>
/// Automatic in-place updater for the control app itself. It downloads the packaged single-file
/// <c>AzerothCoreControl.exe</c> asset from the latest GitHub Release, then swaps it over the running
/// executable via a small self-deleting batch (which waits for this process to exit, replaces the file,
/// and relaunches). Not just a redownload — the running app is actually replaced and restarted.
/// </summary>
public sealed class AppUpdater
{
    private const string ExeAssetName = "AzerothCoreControl.exe";

    private readonly ServerCoordinator _coordinator;
    private CancellationTokenSource? _loopCts;

    public AppUpdater(ServerCoordinator coordinator) => _coordinator = coordinator;

    /// <summary>Raised when the update has been staged and the app must exit so the swap can complete.</summary>
    public event Action? RestartRequired;

    /// <summary>Raised when a background check finds an update but auto-install is off (so the UI can prompt).</summary>
    public event Action<ReleaseInfo>? UpdateAvailable;

    public event Action<AppUpdateStage, string>? StageChanged;
    public event Action<double>? DownloadProgress;

    /// <summary>Begin periodic background checks (respects the AutoCheck / AutoInstall settings).</summary>
    public void StartBackgroundChecks()
    {
        _loopCts = new CancellationTokenSource();
        _ = RunLoopAsync(_loopCts.Token);
    }

    public void Stop() => _loopCts?.Cancel();

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            // Small initial delay so startup isn't blocked by a network call.
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (_coordinator.Settings.AutoCheckForUpdates)
                    await CheckAndMaybeInstallAsync(ct).ConfigureAwait(false);

                var interval = _coordinator.Settings.AppUpdateCheckInterval;
                if (interval < TimeSpan.FromMinutes(5)) interval = TimeSpan.FromMinutes(5);
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    private async Task CheckAndMaybeInstallAsync(CancellationToken ct)
    {
        StageChanged?.Invoke(AppUpdateStage.Checking, "Checking for updates…");
        var release = await _coordinator.Releases.CheckForAppUpdateAsync(ct).ConfigureAwait(false);
        if (release == null)
        {
            StageChanged?.Invoke(AppUpdateStage.UpToDate, "Up to date.");
            return;
        }

        if (_coordinator.Settings.AutoInstallUpdates)
            await DownloadAndApplyAsync(release, ct).ConfigureAwait(false);
        else
            UpdateAvailable?.Invoke(release);
    }

    /// <summary>Check once on demand; returns the newer release or null.</summary>
    public Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
        => _coordinator.Releases.CheckForAppUpdateAsync(ct);

    /// <summary>
    /// Download the new exe and stage the in-place swap, then signal the app to exit so the swap can run.
    /// Only supported for the packaged (self-contained exe) build on Windows.
    /// </summary>
    public async Task DownloadAndApplyAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(currentExe) || !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Auto-install is only available for the packaged .exe build.");

        var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase))
                    ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The release has no .exe asset to install.");

        var dir = Path.GetDirectoryName(currentExe)!;
        var newExe = Path.Combine(dir, "AzerothCoreControl.update.exe");

        StageChanged?.Invoke(AppUpdateStage.Downloading, $"Downloading {release.TagName}…");
        var progress = new Progress<double>(p => DownloadProgress?.Invoke(p));
        await _coordinator.Releases.DownloadAssetAsync(asset, newExe, progress, ct).ConfigureAwait(false);

        StageChanged?.Invoke(AppUpdateStage.Installing, "Installing update…");
        LaunchSwapAndExit(currentExe, newExe);
    }

    /// <summary>
    /// Write and launch a batch that waits for this PID to exit, replaces the exe, relaunches it, and
    /// deletes itself. Then ask the app to shut down.
    /// </summary>
    private void LaunchSwapAndExit(string currentExe, string newExe)
    {
        var pid = Environment.ProcessId;
        var backup = currentExe + ".old";
        var batchPath = Path.Combine(Path.GetTempPath(), $"accontrol-update-{pid}.bat");

        var script = $"""
            @echo off
            :waitloop
            tasklist /fi "PID eq {pid}" | findstr /i "{pid}" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto waitloop
            )
            if exist "{backup}" del /q "{backup}"
            move /y "{currentExe}" "{backup}" >nul
            move /y "{newExe}" "{currentExe}" >nul
            start "" "{currentExe}"
            del /q "{backup}" >nul 2>&1
            del /q "%~f0" >nul 2>&1
            """;
        File.WriteAllText(batchPath, script);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{batchPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        RestartRequired?.Invoke();
    }
}
