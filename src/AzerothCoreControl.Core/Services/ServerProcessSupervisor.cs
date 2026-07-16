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

    private readonly object _gate = new();
    private readonly List<DateTimeOffset> _recentCrashes = new();

    private IProcessHandle? _process;
    private ProcessStartSpec? _currentSpec;
    private ServerState _state = ServerState.Stopped;
    private bool _intentionalStop;
    private int _consecutiveCrashes;
    private int _restartCount;
    private DateTimeOffset? _runningSince;
    private CancellationTokenSource? _restartCts;
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
            LaunchLocked(executablePath, arguments, workingDirectory);
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
        }

        if (graceful && _kind == ServerKind.World)
        {
            // worldserver understands console commands on stdin.
            handle.WriteStdin($".server shutdown {Math.Max(delaySeconds, 1)}");
            var grace = TimeSpan.FromSeconds(delaySeconds + 30);
            if (await handle.WaitForExitAsync(grace, cancellationToken).ConfigureAwait(false))
                return; // exit handler will finalize state
        }
        else if (graceful && _kind == ServerKind.Auth)
        {
            // authserver has no drain command; just give it a moment to close cleanly.
            handle.Kill();
            await handle.WaitForExitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Timed out or non-graceful: force it.
        handle.Kill();
        await handle.WaitForExitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
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

    private void OnOutput(object? sender, string line) => OutputLine?.Invoke(_kind, line);

    private void OnExited(object? sender, EventArgs e)
    {
        if (sender is IProcessHandle handle)
            HandleExit(handle);
    }

    private void HandleExit(IProcessHandle handle)
    {
        SupervisorEvent? notify = null;
        Action? afterUnlock = null;

        lock (_gate)
        {
            // Ignore exits from a stale process we've already replaced.
            if (!ReferenceEquals(handle, _process))
                return;

            int exitCode;
            try { exitCode = handle.ExitCode; }
            catch { exitCode = -1; }

            DetachLocked(handle);
            _runningSince = null;

            if (_intentionalStop)
            {
                _intentionalStop = false;
                _consecutiveCrashes = 0;
                SetStateLocked(ServerState.Stopped);
                notify = new SupervisorEvent(_kind, SupervisorEventKind.StoppedByUser, $"{_kind.DisplayName()} stopped.");
            }
            else
            {
                var classification = ExitCodePolicy.Classify(exitCode);
                _log.LogInformation("{Server} exited with code {Code} ({Class})", _kind, exitCode, classification);

                switch (classification)
                {
                    case ExitClassification.CleanShutdown:
                        _consecutiveCrashes = 0;
                        SetStateLocked(ServerState.Stopped);
                        notify = new SupervisorEvent(_kind, SupervisorEventKind.CleanShutdown,
                            $"{_kind.DisplayName()} shut down cleanly.");
                        break;

                    case ExitClassification.RestartRequested:
                        // Explicit .server restart — honor it regardless of the crash auto-restart toggle.
                        _consecutiveCrashes = 0;
                        notify = new SupervisorEvent(_kind, SupervisorEventKind.Restarting,
                            $"{_kind.DisplayName()} requested a restart.");
                        afterUnlock = () => ScheduleRestart(TimeSpan.Zero);
                        SetStateLocked(ServerState.Restarting);
                        break;

                    case ExitClassification.Crash:
                    default:
                        notify = HandleCrashLocked(exitCode, out afterUnlock);
                        break;
                }
            }
        }

        if (notify != null)
            Notable?.Invoke(notify);
        afterUnlock?.Invoke();
    }

    /// <summary>Crash handling under the lock. Decides between backoff-restart and tripping the breaker.</summary>
    private SupervisorEvent HandleCrashLocked(int exitCode, out Action? afterUnlock)
    {
        afterUnlock = null;
        var now = _time.GetUtcNow();
        var wd = Watchdog;

        _recentCrashes.Add(now);
        _recentCrashes.RemoveAll(t => now - t > wd.CrashWindow);

        if (!wd.AutoRestart)
        {
            SetStateLocked(ServerState.Crashed);
            return new SupervisorEvent(_kind, SupervisorEventKind.Crashed,
                $"{_kind.DisplayName()} crashed (exit {exitCode}). Auto-restart is disabled.");
        }

        if (_recentCrashes.Count >= wd.CrashLoopThreshold)
        {
            SetStateLocked(ServerState.Crashed);
            return new SupervisorEvent(_kind, SupervisorEventKind.CrashLoopTripped,
                $"{_kind.DisplayName()} crashed {_recentCrashes.Count} times in {wd.CrashWindow.TotalMinutes:0} min. " +
                "Auto-restart halted — manual intervention required.");
        }

        _consecutiveCrashes++;
        var delay = ComputeBackoff(wd, _consecutiveCrashes);
        SetStateLocked(ServerState.Restarting);
        afterUnlock = () => ScheduleRestart(delay);
        return new SupervisorEvent(_kind, SupervisorEventKind.Crashed,
            $"{_kind.DisplayName()} crashed (exit {exitCode}). Restarting in {delay.TotalSeconds:0}s " +
            $"(attempt {_consecutiveCrashes}).");
    }

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
        lock (_gate)
        {
            CancelPendingRestart();
            _restartCts = new CancellationTokenSource();
            token = _restartCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _time, token).ConfigureAwait(false);

                lock (_gate)
                {
                    if (token.IsCancellationRequested || _intentionalStop || _currentSpec == null)
                        return;
                    // Re-launch with the same spec used before the exit.
                    _restartCount++;
                    LaunchLocked(_currentSpec.FileName, _currentSpec.Arguments, _currentSpec.WorkingDirectory);
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
