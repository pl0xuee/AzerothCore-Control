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
    }

    public void Start()
    {
        if (_loop != null) return;
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
                await TickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var nowTod = now.TimeOfDay;

        foreach (var job in _settings().Schedules.Where(j => j.Enabled))
        {
            if (job.Days.Count > 0 && !job.Days.Contains(now.DayOfWeek))
                continue;
            // Fire when we're within the same minute as the scheduled time and haven't run today.
            if ((int)nowTod.TotalMinutes != (int)job.TimeOfDay.TotalMinutes)
                continue;
            if (_lastRun.TryGetValue(job.Id, out var last) && last == today)
                continue;

            _lastRun[job.Id] = today;
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
