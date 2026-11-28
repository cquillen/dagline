namespace Dagline.Testing;

/// <summary>
/// A run of a whole graph that a test scripts on its own thread: nothing of the run happens until the test pumps it, so the test
/// makes each thing happen in the order it means, asserts between happenings, and runs the test once.
/// <c>var run = ScriptedRun.Start(graph, options); run.Pump(); run.Run.Stop(); var result = run.Finish(TimeSpan.FromSeconds(30));</c>
/// </summary>
/// <remarks>
/// <para>
/// A scripted run is a real run through Dagline's public API, <see cref="Run.Start"/>, on a host that runs nothing until it is
/// told to. The run's loop turns, its task bodies with whatever their awaits resume, and its reporters' deliveries each wait in a
/// queue of their own. <see cref="Pump"/> runs all three on the calling thread until none has anything queued, and
/// <see cref="PumpLoop"/>, <see cref="PumpBodies"/> and <see cref="PumpReporters"/> each run one, so that a test can put the end
/// of a body before or after a drain or a stop it asks for on <see cref="Run"/>. Asking the run for something only posts to it:
/// the loop applies it when it is next pumped. A body runs under a synchronization context whose posts go back to the bodies'
/// queue, and a reporter's delivery under one of the reporters' own, so an await that resumes on its context stays on the pump.
/// The tasks the run completes, its result and the answer to a checkpoint save, resume their awaiters inline, on the pumping
/// thread, and the token a stop signals runs its callbacks there too.
/// </para>
/// <para>
/// When the options keep the default clock, <see cref="TimeProvider.System"/>, the scripted run puts a clock of its own in its
/// place, and owns it: it starts at midnight UTC on 1 January 2000, its local time zone is UTC, and its time moves only when the
/// test calls <see cref="Advance"/> or <see cref="FireNextTimer"/>, which fire its timers on the calling thread. The run's own
/// timers, the grace period's and the reporter wait's, are on that clock too. A clock passed in the options is used as is, and
/// the run neither moves it nor fires its timers.
/// </para>
/// <para>What the scripted run can promise, and what it cannot:</para>
/// <list type="bullet">
/// <item><description>
/// Pumping until nothing is queued means only that the host's queues are empty, not that the run is waiting for the test. Work
/// that has left the host, such as real I/O in flight or a continuation on the thread pool, is not seen.
/// </description></item>
/// <item><description>
/// A body is exact only when everything it awaits is completed by the test, by the scripted run's clock or by the run, such as a
/// checkpoint save. Anything else, such as real I/O or <see cref="Task.Run(Func{Task})"/>, needs <see cref="PumpUntil"/>: pumping
/// until a condition holds, in real time, with a limit. An await with <c>ConfigureAwait(false)</c> resumes wherever the awaited
/// work completes, and a cancelled delay awaited that way resumes on the thread pool, so it leaves the pump too.
/// </description></item>
/// <item><description>
/// An <see cref="InstantClock"/> fires its timers on the thread pool as soon as they are made, so it cannot make a scripted run
/// exact: a body that delays on it is followed only by <see cref="PumpUntil"/>.
/// </description></item>
/// <item><description>
/// A test must not block on a task whose continuation is queued to the host, such as a body's, or a timer callback that blocks
/// until a body has moved on: nothing runs that continuation until the blocked thread pumps.
/// </description></item>
/// </list>
/// <para>Pumping is for one thread at a time. Asking the run for something, and queuing to the host, are safe from any thread.</para>
/// </remarks>
public sealed class ScriptedRun
{
    private readonly ManualHost _host;
    private readonly ScriptedClock? _clock;
    private readonly Doorbell _doorbell;

    private ScriptedRun(ManualHost host, Doorbell doorbell, ScriptedClock? clock, Run run, TimeProvider timeProvider)
    {
        _host = host;
        _doorbell = doorbell;
        _clock = clock;
        Run = run;
        TimeProvider = timeProvider;
    }

    /// <summary>The run, as an app holds it: drain it, stop it, read its current snapshot and its result.</summary>
    public Run Run { get; }

    /// <summary>
    /// The run's clock: the scripted run's own when it owns one, which only <see cref="Advance"/> and <see cref="FireNextTimer"/>
    /// move, or the clock passed in the options.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Starts a run of <paramref name="graph"/> as a scripted run. Nothing of the run has happened when this returns: its first
    /// turn waits for the test to pump it, and <see cref="Run.Current"/> is the snapshot of a run not yet started.
    /// </summary>
    /// <param name="graph">The graph to run.</param>
    /// <param name="options">
    /// The options of the run, used as they are, except that the default clock, <see cref="TimeProvider.System"/>, is replaced by
    /// a clock the scripted run owns.
    /// </param>
    /// <param name="cancellationToken">The run's token: cancelling it is a stop, as <see cref="Run.Stop"/> is, applied when the loop is next pumped.</param>
    /// <returns>The scripted run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A reporter in <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="RunConfigurationException">The options do not fit the graph.</exception>
    /// <exception cref="DaglineException">The journal chosen in <paramref name="options"/> cannot be opened or read, as for <see cref="Runner.Start"/>.</exception>
    public static ScriptedRun Start(Graph graph, RunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clock = options.TimeProvider == TimeProvider.System ? new ScriptedClock() : null;
        return Start(graph, options, clock, cancellationToken);
    }

    /// <summary>
    /// Starts a scripted run on <paramref name="clock"/>, which it then owns, or, with none, on the clock the options name, as it is.
    /// The task harness passes one clock to every run it makes, so that its time carries from one run to the next.
    /// </summary>
    internal static ScriptedRun Start(Graph graph, RunOptions options, ScriptedClock? clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var doorbell = clock?.Doorbell ?? new Doorbell();
        var host = new ManualHost(doorbell);
        var run = Run.Start(graph, clock is null ? options : options with { TimeProvider = clock }, host, cancellationToken);
        return new ScriptedRun(host, doorbell, clock, run, clock ?? options.TimeProvider);
    }

    /// <summary>
    /// Runs everything queued, and everything that queues while it runs, on the calling thread, until nothing is queued: the loop's
    /// turns, then the bodies' work, then the reporters' deliveries, round again while any has something.
    /// </summary>
    /// <returns>How many items ran.</returns>
    public int Pump() => _host.Pump();

    /// <summary>
    /// Runs the loop's queued turns only, and any queued while they run. One turn applies every input in the run's inbox, so a
    /// drain or a stop asked for, a body's end and a task's reports are applied in the order they were posted.
    /// </summary>
    /// <returns>How many turns ran.</returns>
    public int PumpLoop() => _host.PumpLoop();

    /// <summary>
    /// Runs the queued body starts and the continuations of bodies that came back to the host only, and any queued while they run.
    /// A body that ends posts its end to the run, which is applied at the next <see cref="PumpLoop"/>.
    /// </summary>
    /// <returns>How many items ran.</returns>
    public int PumpBodies() => _host.PumpBodies();

    /// <summary>Runs the queued reporter deliveries only, and any queued while they run.</summary>
    /// <returns>How many items ran.</returns>
    public int PumpReporters() => _host.PumpReporters();

    /// <summary>
    /// Pumps until <paramref name="condition"/> holds on the run's current snapshot, waiting on the calling thread, in real time,
    /// for work to be queued whenever nothing is, for at most <paramref name="realTimeLimit"/>. It never moves the scripted run's
    /// clock. It is for a body that awaits what the pump cannot see, such as real I/O.
    /// </summary>
    /// <param name="condition">Asked after each pump, with <see cref="Run.Current"/>.</param>
    /// <param name="realTimeLimit">The most real time to wait, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <returns><see langword="true"/> once the condition holds; <see langword="false"/> when the limit ran out first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="realTimeLimit"/> is negative other than <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public bool PumpUntil(Func<Snapshot, bool> condition, TimeSpan realTimeLimit)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ThrowIfNegative(realTimeLimit);
        return PumpUntilTrue(() => condition(Run.Current), realTimeLimit);
    }

    /// <summary>
    /// Pumps until <paramref name="condition"/> holds on the run's current snapshot, as <see cref="PumpUntil"/> does, but awaits
    /// work to be queued rather than blocking the thread. After its first wait it goes on pumping on the thread pool, one thread at
    /// a time, so "the test's thread" then means "inside this call".
    /// </summary>
    /// <param name="condition">Asked after each pump, with <see cref="Run.Current"/>.</param>
    /// <param name="realTimeLimit">The most real time to wait, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <returns><see langword="true"/> once the condition holds; <see langword="false"/> when the limit ran out first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="realTimeLimit"/> is negative other than <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public Task<bool> PumpUntilAsync(Func<Snapshot, bool> condition, TimeSpan realTimeLimit)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ThrowIfNegative(realTimeLimit);
        return PumpUntilTrueAsync(() => condition(Run.Current), realTimeLimit);
    }

    /// <summary>
    /// Moves the scripted run's clock on by <paramref name="by"/>. Each timer that comes due on the way fires, in the order they
    /// come due, on the calling thread, with the time moved on to when it is due, and everything its firing queued is pumped
    /// before the next one is looked for. So a body that delays again on the way, such as a wait's next check, has that delay
    /// fire too when it comes due inside <paramref name="by"/>. The time is then where it was plus <paramref name="by"/>.
    /// </summary>
    /// <param name="by">How far to move the clock. Zero fires the timers due now.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="by"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The options named a clock, so the scripted run does not own it.</exception>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        var clock = OwnedClock();
        var target = clock.Elapsed + by;
        while (clock.FireNext(target))
        {
            Pump();
        }

        clock.MoveTo(target);
    }

    /// <summary>
    /// Fires the scripted run's next timer, the one due first, on the calling thread, with the clock's time moved on to when it is
    /// due. Of two due at once, the one set first fires first. It pumps nothing: what the firing queued, such as the continuation
    /// of a body's delay, waits for the next pump.
    /// </summary>
    /// <returns><see langword="false"/> when no timer is set, and then the time does not move.</returns>
    /// <exception cref="InvalidOperationException">The options named a clock, so the scripted run does not own it.</exception>
    public bool FireNextTimer() => OwnedClock().FireNext(TimeSpan.MaxValue);

    /// <summary>
    /// Pumps until the run's result is complete, waiting in real time whenever nothing is queued, for at most
    /// <paramref name="realTimeLimit"/>, and returns the result. It fires no timer: a run whose tasks delay on the scripted run's
    /// clock is moved on with <see cref="Advance"/> or <see cref="FireNextTimer"/> first.
    /// </summary>
    /// <param name="realTimeLimit">The most real time to wait, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <returns>The run's result, as <see cref="Run.Result"/> completes with it.</returns>
    /// <remarks>An exception that <see cref="Run.Result"/> faults with comes out of this call unchanged.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="realTimeLimit"/> is negative other than <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    /// <exception cref="TimeoutException">The run's result was not complete when the limit ran out.</exception>
    public RunResult Finish(TimeSpan realTimeLimit)
    {
        ThrowIfNegative(realTimeLimit);
        if (!PumpUntilTrue(() => Run.Result.IsCompleted, realTimeLimit))
        {
            throw new TimeoutException(
                $"The run's result was not complete after {realTimeLimit} of real time: something the run waits for is outside the pump.");
        }

        // The result is complete, so this waits for nothing: it gives the result, or rethrows what faulted it, unwrapped.
        return Run.Result.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Completes once something is queued or a timer of the scripted run's clock is set, at once if either is so already. The task
    /// harness awaits it whenever there is nothing to pump and no timer to fire, for as long as it takes.
    /// </summary>
    internal Task WhenThereIsSomethingToDoAsync()
    {
        var seen = _doorbell.Rings;
        return _host.HasWork || _clock?.HasTimers is true ? Task.CompletedTask : _doorbell.AfterAsync(seen);
    }

    private bool PumpUntilTrue(Func<bool> condition, TimeSpan realTimeLimit)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            var seen = _doorbell.Rings;
            Pump();
            if (condition())
            {
                return true;
            }

            if (!_host.HasWork && !_doorbell.WaitAfter(seen, Left(started, realTimeLimit)))
            {
                Pump();
                return condition();
            }
        }
    }

    private async Task<bool> PumpUntilTrueAsync(Func<bool> condition, TimeSpan realTimeLimit)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            var seen = _doorbell.Rings;
            Pump();
            if (condition())
            {
                return true;
            }

            if (_host.HasWork)
            {
                continue;
            }

            var left = Left(started, realTimeLimit);
            var rung = _doorbell.AfterAsync(seen);
            if (left == Timeout.InfiniteTimeSpan)
            {
                await rung.ConfigureAwait(false);
            }
            else if (left == TimeSpan.Zero
                || !await RingsWithinAsync(rung, left).ConfigureAwait(false))
            {
                Pump();
                return condition();
            }
        }
    }

    private static async Task<bool> RingsWithinAsync(Task rung, TimeSpan limit)
    {
        try
        {
            await rung.WaitAsync(limit, TimeProvider.System).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static TimeSpan Left(long started, TimeSpan limit)
    {
        if (limit == Timeout.InfiniteTimeSpan)
        {
            return limit;
        }

        var left = limit - TimeProvider.System.GetElapsedTime(started);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private ScriptedClock OwnedClock() => _clock ?? throw new InvalidOperationException(
        "The run is on the clock its options name, which the scripted run does not own: move that clock itself.");

    private static void ThrowIfNegative(TimeSpan realTimeLimit)
    {
        if (realTimeLimit != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(realTimeLimit, TimeSpan.Zero, nameof(realTimeLimit));
        }
    }
}
