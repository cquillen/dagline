namespace Dagline.Testing.Tests;

public class InstantClockTests
{
    private static readonly DateTimeOffset _start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_delay_of_an_hour_completes_at_once_and_the_time_is_an_hour_later()
    {
        var clock = new InstantClock();
        var started = clock.GetTimestamp();

        await Task.Delay(TimeSpan.FromHours(1), clock);

        clock.GetUtcNow().ShouldBe(_start.AddHours(1));
        clock.GetElapsedTime(started).ShouldBe(TimeSpan.FromHours(1));
    }

    [Test]
    public async Task Two_clocks_made_at_different_moments_report_the_same_time_in_utc()
    {
        var first = new InstantClock();
        await Task.Delay(TimeSpan.FromMinutes(5), first);
        await Task.Yield();

        var second = new InstantClock();

        second.GetUtcNow().ShouldBe(_start);
        second.GetLocalNow().ShouldBe(_start);
        second.LocalTimeZone.ShouldBe(TimeZoneInfo.Utc);
    }

    [Test]
    public async Task With_several_delays_in_flight_the_time_advances_by_each_and_never_moves_backwards()
    {
        var clock = new InstantClock();
        var started = clock.GetTimestamp();
        var seen = new List<DateTimeOffset>();

        await Task.WhenAll(
            Task.Delay(TimeSpan.FromMinutes(1), clock).ContinueWith(_ => { lock (seen) { seen.Add(clock.GetUtcNow()); } }, TaskScheduler.Default),
            Task.Delay(TimeSpan.FromMinutes(2), clock).ContinueWith(_ => { lock (seen) { seen.Add(clock.GetUtcNow()); } }, TaskScheduler.Default),
            Task.Delay(TimeSpan.FromMinutes(3), clock).ContinueWith(_ => { lock (seen) { seen.Add(clock.GetUtcNow()); } }, TaskScheduler.Default));

        clock.GetUtcNow().ShouldBe(_start.AddMinutes(6));
        clock.GetElapsedTime(started).ShouldBe(TimeSpan.FromMinutes(6));
        seen.Count.ShouldBe(3);
        seen.ShouldAllBe(time => time > _start && time <= _start.AddMinutes(6));
    }

    [Test]
    public async Task A_periodic_timer_fires_repeatedly_moving_the_time_by_its_period_and_stops_when_disposed()
    {
        var clock = new InstantClock();
        var firings = new List<DateTimeOffset>();
        var thirdFiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var disposed = new ManualResetEventSlim();

        var timer = clock.CreateTimer(
            _ =>
            {
                firings.Add(clock.GetUtcNow());
                if (firings.Count == 3)
                {
                    thirdFiring.SetResult();
                    disposed.Wait();
                }
            },
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10));
        await thirdFiring.Task;
        timer.Dispose();
        disposed.Set();
        await Task.Delay(TimeSpan.FromSeconds(1), clock);

        firings.ShouldBe([_start.AddMinutes(1), _start.AddMinutes(11), _start.AddMinutes(21)]);
        clock.GetUtcNow().ShouldBe(_start.AddMinutes(21).AddSeconds(1));
    }

    [Test]
    public async Task A_callback_never_runs_inside_the_call_that_created_its_timer()
    {
        var clock = new InstantClock();
        var creatingThread = Environment.CurrentManagedThreadId;
        var returned = false;
        var ranInside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timer = clock.CreateTimer(
            _ => ranInside.SetResult(Environment.CurrentManagedThreadId == creatingThread && !Volatile.Read(ref returned)),
            null,
            TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);
        Volatile.Write(ref returned, true);

        (await ranInside.Task).ShouldBeFalse();
    }

    [Test]
    public async Task Past_the_guard_making_a_delay_or_a_timer_throws_a_clock_guard_exception()
    {
        var clock = new InstantClock(TimeSpan.FromHours(1));
        await Task.Delay(TimeSpan.FromHours(1), clock);
        await Task.Delay(TimeSpan.FromMilliseconds(1), clock);

        var delay = Should.Throw<ClockGuardException>(() => Task.Delay(TimeSpan.FromSeconds(1), clock));
        Should.Throw<ClockGuardException>(() => clock.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));

        delay.ShouldNotBeAssignableTo<DaglineException>();
    }

    [Test]
    public async Task The_default_guard_is_seven_days_of_fake_time()
    {
        var clock = new InstantClock();
        await Task.Delay(TimeSpan.FromDays(7), clock);
        await Task.Delay(TimeSpan.FromMilliseconds(1), clock);

        Should.Throw<ClockGuardException>(() => Task.Delay(TimeSpan.FromSeconds(1), clock));
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void A_guard_of_zero_or_less_is_refused(int seconds) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new InstantClock(TimeSpan.FromSeconds(seconds)));
}
