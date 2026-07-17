using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Services;
using AzerothCoreControl.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

/// <summary>
/// Covers WHEN jobs fire. The job bodies are irrelevant here — the JobStarting seam records the scheduler's
/// decision and the date it attributed the slot to.
/// </summary>
public class ScheduleServiceTests
{
    private sealed record Fired(string JobId, DateOnly DueDate);

    private static (ScheduleService svc, AppSettings settings, FakeTimeProvider time, List<Fired> fired)
        Create(DateTimeOffset startedAt)
    {
        var settings = new AppSettings();
        settings.MySql.Databases = new List<string>(); // a Backup job then has nothing to dump and returns fast

        var time = new FakeTimeProvider(startedAt);
        time.SetLocalTimeZone(TimeZoneInfo.Utc); // keep local == UTC so the test times read literally

        var launcher = new FakeProcessLauncher();
        var world = new ServerProcessSupervisor(ServerKind.World, launcher, () => settings, time);
        var auth = new ServerProcessSupervisor(ServerKind.Auth, launcher, () => settings, time);
        var svc = new ScheduleService(() => settings, world, auth, new BackupService(() => settings, time), time);

        var fired = new List<Fired>();
        svc.JobStarting += (job, due) => fired.Add(new Fired(job.Id, due));
        return (svc, settings, time, fired);
    }

    private static ScheduledJob Job(TimeSpan at) => new()
    {
        Name = "test",
        Kind = ScheduledJobKind.Backup,
        TimeOfDay = at,
        Enabled = true,
    };

    private static DateTimeOffset At(int day, int hour, int minute)
        => new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task FiresWithinTheCatchUpWindow_NotOnlyOnTheExactMinute()
    {
        // Regression: a tick landing at 03:06 (ticks collapse while a long job runs) skipped a 03:00 job
        // entirely — it fired only if a tick hit the exact scheduled minute.
        var (svc, settings, time, fired) = Create(At(16, 2, 0));
        var job = Job(new TimeSpan(3, 0, 0));
        settings.Schedules = new List<ScheduledJob> { job };

        time.SetUtcNow(At(16, 3, 6));
        await svc.TickAsync(CancellationToken.None);

        Assert.Single(fired);
        Assert.Equal(new DateOnly(2026, 7, 16), fired[0].DueDate);
    }

    [Fact]
    public async Task DoesNotFireOnceTheWindowHasPassed()
    {
        var (svc, settings, time, fired) = Create(At(16, 2, 0));
        settings.Schedules = new List<ScheduledJob> { Job(new TimeSpan(3, 0, 0)) };

        time.SetUtcNow(At(16, 3, 30)); // half an hour late — that slot is gone
        await svc.TickAsync(CancellationToken.None);

        Assert.Empty(fired);
    }

    [Fact]
    public async Task LateEveningJob_CaughtUpAcrossMidnight_StillFires()
    {
        // Regression: the window didn't wrap, so a 23:58 job evaluated at 00:04 computed a negative
        // "since due" and was skipped — and the date had rolled over, so it never fired at all.
        var (svc, settings, time, fired) = Create(At(16, 20, 0));
        settings.Schedules = new List<ScheduledJob> { Job(new TimeSpan(23, 58, 0)) };

        time.SetUtcNow(At(17, 0, 4));
        await svc.TickAsync(CancellationToken.None);

        Assert.Single(fired);
        Assert.Equal(new DateOnly(2026, 7, 16), fired[0].DueDate); // the 16th's slot, not the 17th's
    }

    [Fact]
    public async Task ASlotThatPredatesStartup_IsNotRerun()
    {
        // _lastRun is in-memory, so after a relaunch we can't know whether a slot already ran. Re-firing a
        // restart job would kick the realm a second time — the catch-up window must not enable that.
        var (svc, settings, time, fired) = Create(At(16, 4, 6)); // app started 6 min AFTER the slot
        settings.Schedules = new List<ScheduledJob> { Job(new TimeSpan(4, 0, 0)) };

        time.SetUtcNow(At(16, 4, 7));
        await svc.TickAsync(CancellationToken.None);

        Assert.Empty(fired);
    }

    [Fact]
    public async Task FiresOnlyOncePerDay()
    {
        var (svc, settings, time, fired) = Create(At(16, 2, 0));
        settings.Schedules = new List<ScheduledJob> { Job(new TimeSpan(3, 0, 0)) };

        time.SetUtcNow(At(16, 3, 0));
        await svc.TickAsync(CancellationToken.None);
        time.SetUtcNow(At(16, 3, 2)); // still inside the catch-up window
        await svc.TickAsync(CancellationToken.None);

        Assert.Single(fired);
    }

    [Fact]
    public async Task FiresAgainTheNextDay()
    {
        var (svc, settings, time, fired) = Create(At(16, 2, 0));
        settings.Schedules = new List<ScheduledJob> { Job(new TimeSpan(3, 0, 0)) };

        time.SetUtcNow(At(16, 3, 0));
        await svc.TickAsync(CancellationToken.None);
        time.SetUtcNow(At(17, 3, 0));
        await svc.TickAsync(CancellationToken.None);

        Assert.Equal(2, fired.Count);
        Assert.Equal(new DateOnly(2026, 7, 17), fired[1].DueDate);
    }

    [Fact]
    public async Task DayOfWeekIsCheckedAgainstTheDueDate_NotNow()
    {
        // A Thursday-only 23:58 job caught up at 00:04 on Friday must still run: the slot was Thursday's.
        var (svc, settings, time, fired) = Create(At(16, 20, 0)); // 2026-07-16 is a Thursday
        var job = Job(new TimeSpan(23, 58, 0));
        job.Days = new List<DayOfWeek> { DayOfWeek.Thursday };
        settings.Schedules = new List<ScheduledJob> { job };

        time.SetUtcNow(At(17, 0, 4)); // now it's Friday
        await svc.TickAsync(CancellationToken.None);

        Assert.Single(fired);
    }

    [Fact]
    public async Task ADisabledJobNeverFires()
    {
        var (svc, settings, time, fired) = Create(At(16, 2, 0));
        var job = Job(new TimeSpan(3, 0, 0));
        job.Enabled = false;
        settings.Schedules = new List<ScheduledJob> { job };

        time.SetUtcNow(At(16, 3, 0));
        await svc.TickAsync(CancellationToken.None);

        Assert.Empty(fired);
    }
}
