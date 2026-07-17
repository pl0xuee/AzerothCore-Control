using AzerothCoreControl.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

/// <summary>
/// Fires scheduled restart/backup jobs at their configured local time-of-day. A single background
/// loop ticks once a minute and runs any job whose minute has arrived (tracking the last run day so a
/// job fires at most once per day). Restart jobs broadcast staged in-game warnings before restarting.
/// </summary>
public sealed class ScheduleService : IAsyncDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly ServerProcessSupervisor _world;
    private readonly ServerProcessSupervisor _auth;
    private readonly BackupService _backup;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private readonly Dictionary<string, DateOnly> _lastRun = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>When supervision began — slots due before this belong to a previous run we can't vouch for.</summary>
    private DateTimeOffset _startedAt;

    public ScheduleService(
        Func<AppSettings> settings,
        ServerProcessSupervisor world,
        ServerProcessSupervisor auth,
        BackupService backup,
        TimeProvider? time = null,
        ILogger<ScheduleService>? logger = null)
    {
        _settings = settings;
        _world = world;
        _auth = auth;
        _backup = backup;
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger<ScheduleService>.Instance;
        _startedAt = _time.GetLocalNow();
    }

    /// <summary>
    /// Test seam: raised for each job the scheduler decides to run, with the date the slot belonged to.
    /// (_lastRun can't serve this purpose — a slot that predates startup is recorded there precisely so it
    /// won't run, so it cannot distinguish "ran" from "deliberately suppressed".)
    /// </summary>
    internal event Action<ScheduledJob, DateOnly>? JobStarting;

    public void Start()
    {
        if (_loop != null) return;
        _startedAt = _time.GetLocalNow();
        _cts = new CancellationTokenSource();
        _loop = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Align to the top of each minute using the injected clock.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // One bad tick must never end the loop: this task is only awaited on shutdown, so anything
                // escaping here would fault it silently and every future backup/restart would stop firing
                // until the app was restarted, with nothing shown to the user.
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Scheduler tick failed; the schedule remains active");
                }
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    /// <summary>
    /// How late a job may still run. Long enough to absorb a job that overruns its slot, short enough that
    /// launching the app in the afternoon doesn't trigger the 03:00 backup.
    /// </summary>
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(10);

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var nowTod = now.TimeOfDay;

        // Read the list reference once. The UI thread swaps in a whole new list when jobs are added or
        // removed (see SchedulesViewModel), so this enumerates a stable snapshot rather than a collection
        // that could be mutated mid-iteration.
        var jobs = _settings().Schedules;

        foreach (var job in jobs.Where(j => j.Enabled))
        {
            // Fire once per due-date, any time within the catch-up window after the scheduled minute.
            // Matching the exact minute silently skipped jobs: ticks are 30s apart but PeriodicTimer
            // collapses missed ticks, so a job that runs long (a 6-minute DB dump) ate the next job's minute.
            var sinceDue = nowTod - job.TimeOfDay;
            var dueDate = today;
            if (sinceDue < TimeSpan.Zero)
            {
                // Before today's slot — but we may be just past YESTERDAY's (a 23:58 job caught up at 00:04).
                sinceDue += TimeSpan.FromDays(1);
                dueDate = today.AddDays(-1);
            }
            if (sinceDue > CatchUpWindow)
                continue;

            // Day-of-week and the once-per-day key both belong to the day the job was DUE, not to "now" —
            // they differ for a job caught up across midnight.
            if (job.Days.Count > 0 && !job.Days.Contains(dueDate.DayOfWeek))
                continue;
            if (_lastRun.TryGetValue(job.Id, out var last) && last == dueDate)
                continue;

            // Only catch up on slots we were actually around for. _lastRun is in-memory, so after a relaunch
            // (including the app's own update-swap restart) we cannot know whether a slot already ran — and
            // re-firing a restart job would kick the realm a second time.
            // Compare wall-clock to wall-clock: .DateTime keeps the offset the clock reported, whereas
            // .LocalDateTime would re-project into the MACHINE's time zone and disagree with `now` above.
            var dueMoment = dueDate.ToDateTime(TimeOnly.FromTimeSpan(job.TimeOfDay));
            if (dueMoment < _startedAt.DateTime)
            {
                _lastRun[job.Id] = dueDate; // treat as handled, so it isn't reconsidered every tick
                continue;
            }

            _lastRun[job.Id] = dueDate;
            JobStarting?.Invoke(job, dueDate);
            _log.LogInformation("Running scheduled job {Name} ({Kind})", job.Name, job.Kind);
            try
            {
                await RunJobAsync(job, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Scheduled job {Name} failed", job.Name);
            }
        }
    }

    private async Task RunJobAsync(ScheduledJob job, CancellationToken ct)
    {
        switch (job.Kind)
        {
            case ScheduledJobKind.Backup:
                await _backup.BackupAsync(cancellationToken: ct).ConfigureAwait(false);
                break;

            case ScheduledJobKind.Restart:
                await RestartWithWarningsAsync(ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Trigger a graceful restart. <c>.server restart &lt;delay&gt;</c> makes worldserver broadcast its own
    /// staged in-game countdown and then exit with the RESTART exit code, which the supervisor treats as
    /// a restart request and relaunches automatically.
    /// </summary>
    public Task RestartWithWarningsAsync(CancellationToken ct = default)
    {
        if (_world.State == ServerState.Running)
        {
            var delay = Math.Max(_settings().Watchdog.GracefulShutdownSeconds, 60);
            _world.SendConsole($".announce Scheduled restart in {delay / 60} minute(s).");
            _world.SendConsole($".server restart {delay}");
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts?.Dispose();
    }
}
