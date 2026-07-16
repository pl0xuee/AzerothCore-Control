using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

/// <summary>A notable lifecycle event worth surfacing to the UI / notifications.</summary>
public enum SupervisorEventKind
{
    Started,
    StoppedByUser,
    CleanShutdown,
    Restarting,
    Crashed,
    CrashLoopTripped,
}

public sealed record SupervisorEvent(ServerKind Server, SupervisorEventKind Kind, string Message);

/// <summary>
/// Supervises a single AzerothCore server process: launches it, streams its console output,
/// and auto-restarts it according to AzerothCore's exit-code protocol with exponential backoff
/// and a crash-loop breaker.
///
/// Thread-safety: all state transitions are serialized on <see cref="_gate"/>. Process events
/// arrive on background threads and are marshalled through the same lock.
/// </summary>
public sealed class ServerProcessSupervisor : IDisposable
{
    private readonly ServerKind _kind;
    private readonly IProcessLauncher _launcher;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private const int RecentOutputLimit = 20;

    private readonly object _gate = new();
    private readonly List<DateTimeOffset> _recentCrashes = new();
    private readonly Queue<string> _recentOutput = new();

    private IProcessHandle? _process;
    private ProcessStartSpec? _currentSpec;
    private ServerState _state = ServerState.Stopped;
    private bool _intentionalStop;
    private int _consecutiveCrashes;
    private int _consecutiveQuickCrashes;
    private int _restartCount;
    private DateTimeOffset? _runningSince;
    private CancellationTokenSource? _restartCts;
    private TaskCompletionSource? _stopCompletion;
    private int _restartGeneration;
    private int _disposed;

    public ServerProcessSupervisor(
        ServerKind kind,
        IProcessLauncher launcher,
        Func<AppSettings> settingsAccessor,
        TimeProvider? timeProvider = null,
        ILogger<ServerProcessSupervisor>? logger = null)
    {
        _kind = kind;
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _settingsAccessor = settingsAccessor ?? throw new ArgumentNullException(nameof(settingsAccessor));
        _time = timeProvider ?? TimeProvider.System;
        _log = logger ?? NullLogger<ServerProcessSupervisor>.Instance;
    }

    public ServerKind Kind => _kind;

    /// <summary>Current lifecycle state.</summary>
    public ServerState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Number of automatic restarts performed this session.</summary>
    public int RestartCount
    {
        get { lock (_gate) return _restartCount; }
    }

    /// <summary>When the current process started, or null if not running.</summary>
    public DateTimeOffset? RunningSince
    {
        get { lock (_gate) return _runningSince; }
    }

    /// <summary>Raised on every state transition (new state).</summary>
    public event Action<ServerKind, ServerState>? StateChanged;

    /// <summary>A console/stdout/stderr line from the process.</summary>
    public event Action<ServerKind, string>? OutputLine;

    /// <summary>A notable event for notifications (crash, breaker tripped, etc.).</summary>
    public event Action<SupervisorEvent>? Notable;

    private WatchdogSettings Watchdog => _settingsAccessor().Watchdog;

    /// <summary>Start the server if it isn't already running.</summary>
    public void Start(string executablePath, string arguments = "", string? workingDirectory = null)
    {
        lock (_gate)
        {
            if (_state is ServerState.Running or ServerState.Starting)
                return;
            CancelPendingRestart();
            _intentionalStop = false;
            try
            {
                LaunchLocked(executablePath, arguments, workingDirectory);
            }
            catch
            {
                // Launch failed (e.g. missing exe) — don't leave the state stuck at "Starting".
                _process = null;
                _currentSpec = null;
                _runningSince = null;
                SetStateLocked(ServerState.Stopped);
                throw;
            }
        }
    }

    /// <summary>
    /// Gracefully stop the server: send <c>.server shutdown &lt;delay&gt;</c> (worldserver) so players are
    /// warned and data is saved, then wait for exit; falls back to a hard kill on timeout.
    /// </summary>
    public async Task StopAsync(bool graceful = true, CancellationToken cancellationToken = default)
    {
        IProcessHandle? handle;
        int delaySeconds;
        Task stopped;
        lock (_gate)
        {
            CancelPendingRestart();
            _intentionalStop = true;
            handle = _process;
            delaySeconds = Watchdog.GracefulShutdownSeconds;
            if (handle == null || _state == ServerState.Stopped)
            {
                SetStateLocked(ServerState.Stopped);
                return;
            }
            // Completed by HandleExit once the state has actually settled to Stopped — so callers that
            // restart immediately after StopAsync (e.g. "Restart world") don't race the exit handler.
            _stopCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stopped = _stopCompletion.Task;
        }

        if (graceful && _kind == ServerKind.World)
        {
            // worldserver understands console commands on stdin.
            handle.WriteStdin($".server shutdown {Math.Max(delaySeconds, 1)}");
            var grace = TimeSpan.FromSeconds(delaySeconds + 30);
            if (await WaitOrTimeoutAsync(stopped, grace, cancellationToken).ConfigureAwait(false))
                return; // exit handler finalized the state
        }
        else if (graceful && _kind == ServerKind.Auth)
        {
            // authserver has no drain command; just give it a moment to close cleanly.
            handle.Kill();
            await WaitOrTimeoutAsync(stopped, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Timed out or non-graceful: force it, then wait for the state to settle.
        handle.Kill();
        await WaitOrTimeoutAsync(stopped, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Await <paramref name="task"/> up to <paramref name="timeout"/>; true if it completed in time.</summary>
    private async Task<bool> WaitOrTimeoutAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout, _time, cancellationToken)).ConfigureAwait(false);
        return completed == task;
    }

    /// <summary>
    /// Snapshot the running process's memory (working set) and cumulative CPU time.
    /// Returns false when not running. The caller computes CPU% from deltas between snapshots.
    /// </summary>
    public bool TryGetResourceSnapshot(out long workingSetBytes, out TimeSpan cpuTime)
    {
        lock (_gate)
        {
            if (_process != null && _state == ServerState.Running)
            {
                workingSetBytes = _process.WorkingSetBytes;
                cpuTime = _process.TotalProcessorTime;
                return true;
            }
        }
        workingSetBytes = 0;
        cpuTime = TimeSpan.Zero;
        return false;
    }

    /// <summary>Send a raw line to the process's stdin (worldserver console command).</summary>
    public void SendConsole(string command)
    {
        IProcessHandle? handle;
        lock (_gate) handle = _process;
        handle?.WriteStdin(command);
    }

    // ---- internals ---------------------------------------------------------

    private void LaunchLocked(string executablePath, string arguments, string? workingDirectory)
    {
        SetStateLocked(ServerState.Starting);
        _log.LogInformation("Starting {Server} from {Path}", _kind, executablePath);

        var spec = new ProcessStartSpec(executablePath, arguments, workingDirectory);
        _currentSpec = spec;
        var handle = _launcher.Launch(spec);
        _process = handle;
        _runningSince = _time.GetUtcNow();

        handle.OutputLine += OnOutput;
        handle.ErrorLine += OnOutput;
        handle.Exited += OnExited;

        SetStateLocked(ServerState.Running);
        Notable?.Invoke(new SupervisorEvent(_kind, SupervisorEventKind.Started, $"{_kind.DisplayName()} started."));

        // Race guard: the process may have exited between Launch() and event wiring.
        if (handle.HasExited)
            HandleExit(handle);
    }

    private void OnOutput(object? sender, string line)
    {
        lock (_gate)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > RecentOutputLimit)
                _recentOutput.Dequeue();
        }
        OutputLine?.Invoke(_kind, line);
    }

    /// <summary>Last non-empty output line, for diagnosing why a server exited (called under the lock).</summary>
    private string? LastMeaningfulOutputLocked()
    {
        for (var i = _recentOutput.Count - 1; i >= 0; i--)
        {
            var line = _recentOutput.ElementAt(i).Trim();
            if (line.Length > 0)
                return line;
        }
        return null;
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (sender is IProcessHandle handle)
            HandleExit(handle);
    }

    private void HandleExit(IProcessHandle handle)
    {
        SupervisorEvent? notify = null;
        Action? afterUnlock = null;
        TaskCompletionSource? stopToSignal = null;

        lock (_gate)
        {
            // Ignore exits from a stale process we've already replaced.
            if (!ReferenceEquals(handle, _process))
                return;

            int exitCode;
            try { exitCode = handle.ExitCode; }
            catch { exitCode = -1; }

            // How long did it run? A server that exits within a few seconds almost certainly failed to
            // start (bad config, DB unreachable, missing files) rather than being stopped normally.
            var ranFor = _runningSince is { } started ? _time.GetUtcNow() - started : (TimeSpan?)null;
            var quickExit = ranFor is { } r && r < Watchdog.StartupFailureWindow;
            var lastOutput = LastMeaningfulOutputLocked();

            DetachLocked(handle);
            _runningSince = null;

            if (_intentionalStop)
            {
                _intentionalStop = false;
                _consecutiveCrashes = 0;
                _consecutiveQuickCrashes = 0;
                SetStateLocked(ServerState.Stopped);
                notify = new SupervisorEvent(_kind, SupervisorEventKind.StoppedByUser, $"{_kind.DisplayName()} stopped.");
            }
            else
            {
                var classification = ExitCodePolicy.Classify(exitCode);
                // Only worldserver has a restart command (RESTART_EXIT_CODE=1). For authserver, exit 1 is
                // just an error — treat it as a crash so it can't infinite-restart with no backoff.
                if (_kind != ServerKind.World && classification == ExitClassification.RestartRequested)
                    classification = ExitClassification.Crash;

                _log.LogInformation("{Server} exited with code {Code} ({Class}) after {Seconds:0}s. Last output: {Output}",
                    _kind, exitCode, classification, ranFor?.TotalSeconds ?? 0, lastOutput ?? "(none)");

                switch (classification)
                {
                    case ExitClassification.CleanShutdown when quickExit:
                        // Exited "cleanly" but almost immediately — surface it as a probable startup failure.
                        _consecutiveCrashes = 0;
                        SetStateLocked(ServerState.Stopped);
                        notify = new SupervisorEvent(_kind, SupervisorEventKind.Crashed,
                            $"{_kind.DisplayName()} exited immediately after starting — likely a startup problem. " +
                            Reason(lastOutput));
                        break;

                    case ExitClassification.CleanShutdown:
                        _consecutiveCrashes = 0;
                        _consecutiveQuickCrashes = 0;
                        SetStateLocked(ServerState.Stopped);
                        notify = new SupervisorEvent(_kind, SupervisorEventKind.CleanShutdown,
                            $"{_kind.DisplayName()} shut down cleanly.");
                        break;

                    case ExitClassification.RestartRequested when quickExit:
                        // Asked to "restart" but died immediately — it's failing to start, not restarting.
                        // Route through the crash handler so the fast breaker can stop the loop.
                        notify = HandleCrashLocked(exitCode, lastOutput, quickExit: true, out afterUnlock);
                        break;

                    case ExitClassification.RestartRequested:
                        // Explicit, legitimate .server restart after running for a while.
                        _consecutiveCrashes = 0;
                        _consecutiveQuickCrashes = 0;
                        notify = new SupervisorEvent(_kind, SupervisorEventKind.Restarting,
                            $"{_kind.DisplayName()} requested a restart.");
                        afterUnlock = () => ScheduleRestart(TimeSpan.Zero);
                        SetStateLocked(ServerState.Restarting);
                        break;

                    case ExitClassification.Crash:
                    default:
                        notify = HandleCrashLocked(exitCode, lastOutput, quickExit, out afterUnlock);
                        break;
                }
            }

            // Hand off any pending stop-completion; completed after the lock (state is now settled).
            stopToSignal = _stopCompletion;
            _stopCompletion = null;
        }

        if (notify != null)
            Notable?.Invoke(notify);
        afterUnlock?.Invoke();
        stopToSignal?.TrySetResult();
    }

    /// <summary>Crash handling under the lock. Decides between backoff-restart and tripping the breaker.</summary>
    private SupervisorEvent HandleCrashLocked(int exitCode, string? lastOutput, bool quickExit, out Action? afterUnlock)
    {
        afterUnlock = null;
        var now = _time.GetUtcNow();
        var wd = Watchdog;

        _recentCrashes.Add(now);
        _recentCrashes.RemoveAll(t => now - t > wd.CrashWindow);
        _consecutiveQuickCrashes = quickExit ? _consecutiveQuickCrashes + 1 : 0;

        if (!wd.AutoRestart)
        {
            SetStateLocked(ServerState.Crashed);
            return new SupervisorEvent(_kind, SupervisorEventKind.Crashed,
                $"{_kind.DisplayName()} crashed (exit {exitCode}). Auto-restart is disabled. {Reason(lastOutput)}");
        }

        // Fast breaker: a server that dies almost immediately, repeatedly, isn't going to start — stop
        // hammering it (which otherwise churns restarts/notifications) and say why.
        if (_consecutiveQuickCrashes >= wd.StartupFailureLimit)
        {
            SetStateLocked(ServerState.Crashed);
            return new SupervisorEvent(_kind, SupervisorEventKind.CrashLoopTripped,
                $"{_kind.DisplayName()} keeps failing to start ({_consecutiveQuickCrashes} times in a row). " +
                $"Auto-restart halted — check the configuration/database. {Reason(lastOutput)}");
        }

        if (_recentCrashes.Count >= wd.CrashLoopThreshold)
        {
            SetStateLocked(ServerState.Crashed);
            return new SupervisorEvent(_kind, SupervisorEventKind.CrashLoopTripped,
                $"{_kind.DisplayName()} crashed {_recentCrashes.Count} times in {wd.CrashWindow.TotalMinutes:0} min. " +
                $"Auto-restart halted — manual intervention required. {Reason(lastOutput)}");
        }

        _consecutiveCrashes++;
        var delay = ComputeBackoff(wd, _consecutiveCrashes);
        SetStateLocked(ServerState.Restarting);
        afterUnlock = () => ScheduleRestart(delay);
        return new SupervisorEvent(_kind, SupervisorEventKind.Crashed,
            $"{_kind.DisplayName()} crashed (exit {exitCode}). Restarting in {delay.TotalSeconds:0}s " +
            $"(attempt {_consecutiveCrashes}). {Reason(lastOutput)}");
    }

    /// <summary>Format the last server output line as a human hint, if any.</summary>
    private static string Reason(string? lastOutput)
        => string.IsNullOrWhiteSpace(lastOutput) ? "" : $"Last message: \"{Truncate(lastOutput, 160)}\"";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Exponential backoff: initial * 2^(n-1), capped at max.</summary>
    internal static TimeSpan ComputeBackoff(WatchdogSettings wd, int consecutiveCrashes)
    {
        var factor = Math.Pow(2, Math.Max(0, consecutiveCrashes - 1));
        var seconds = wd.InitialBackoff.TotalSeconds * factor;
        seconds = Math.Min(seconds, wd.MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private void ScheduleRestart(TimeSpan delay)
    {
        CancellationToken token;
        int generation;
        lock (_gate)
        {
            CancelPendingRestart();
            _restartCts = new CancellationTokenSource();
            token = _restartCts.Token;
            generation = ++_restartGeneration;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _time, token).ConfigureAwait(false);

                lock (_gate)
                {
                    // Only relaunch if this is still the current pending restart AND nothing else has
                    // started/stopped the server in the meantime (guards against a duplicate process).
                    if (token.IsCancellationRequested || _intentionalStop || _currentSpec == null
                        || generation != _restartGeneration || _state != ServerState.Restarting)
                        return;

                    try
                    {
                        _restartCount++;
                        LaunchLocked(_currentSpec.FileName, _currentSpec.Arguments, _currentSpec.WorkingDirectory);
                    }
                    catch (Exception ex)
                    {
                        // A failed relaunch (exe locked mid-swap, missing, etc.) must not leave us stuck
                        // in "Starting" — fall back to Crashed so the user can retry.
                        _log.LogError(ex, "Failed to relaunch {Server}", _kind);
                        _process = null;
                        _runningSince = null;
                        SetStateLocked(ServerState.Crashed);
                    }
                }
            }
            catch (OperationCanceledException) { /* restart cancelled by user stop */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to restart {Server}", _kind);
            }
        }, token);
    }

    private void DetachLocked(IProcessHandle handle)
    {
        handle.OutputLine -= OnOutput;
        handle.ErrorLine -= OnOutput;
        handle.Exited -= OnExited;
        handle.Dispose();
        if (ReferenceEquals(_process, handle))
            _process = null;
    }

    private void CancelPendingRestart()
    {
        _restartCts?.Cancel();
        _restartCts?.Dispose();
        _restartCts = null;
    }

    private void SetStateLocked(ServerState state)
    {
        if (_state == state)
            return;
        _state = state;
        StateChanged?.Invoke(_kind, state);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_gate)
        {
            CancelPendingRestart();
            _intentionalStop = true;
            if (_process != null)
            {
                _process.Kill();
                _process.Dispose();
                _process = null;
            }
        }
    }
}
