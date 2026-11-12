namespace Dagline.Testing;

/// <summary>
/// A clock on which every finite delay and timer ends at once, and whose time jumps forward by the delay as it does. Pass it as
/// <see cref="RunOptions.TimeProvider"/>, and a graph whose tasks wait for hours completes in milliseconds. A delay or timer
/// with an infinite due time never ends.
/// </summary>
/// <remarks>
/// <para>
/// The clock starts at midnight UTC on 1 January 2000, the same instant for every clock made, so a test sees the same times on
/// every run. Its <see cref="LocalTimeZone"/> is UTC. Time only moves forward, and only when a timer made from the clock fires:
/// a timer due after a delay fires as soon as the thread pool runs it, moving the time forward by that delay first. With several
/// delays in flight the time advances by each of them. <see cref="GetTimestamp"/> counts the same time as
/// <see cref="GetUtcNow"/>, so elapsed time measured either way is the same.
/// </para>
/// <para>
/// A callback never runs inside the call that created or changed its timer: it is queued to the thread pool. A periodic timer
/// fires again and again, each firing moving the time forward by its period, until it is disposed or changed. The guard does not
/// stop it: the guard is checked only when a delay or timer is made, so a periodic timer already made keeps firing past it. Do
/// not hand the clock to anything that keeps a periodic timer for the life of a run, such as a reporter: that timer would keep
/// firing and spin.
/// </para>
/// <para>
/// The clock is safe to use from several threads at once.
/// </para>
/// </remarks>
public sealed class InstantClock : TimeProvider
{
    private static readonly DateTimeOffset _start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _defaultGuard = TimeSpan.FromDays(7);

    private static readonly TimeSpan _maxTime = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly TimeSpan _guard;
    private long _elapsedTicks;

    /// <summary>Creates a clock at its fixed starting instant.</summary>
    /// <param name="guard">
    /// The most fake time the clock may pass before it refuses to make another delay or timer. Once more than this has passed,
    /// <see cref="CreateTimer"/>, and so every <see cref="Task.Delay(TimeSpan, TimeProvider)"/> on the clock, throws
    /// <see cref="ClockGuardException"/>. Inside a task body that fails the task as an unexpected failure, so a check that never
    /// returns <see langword="true"/> ends the task instead of spinning for ever. The default is 7 days. Set it higher for a task
    /// that legitimately waits for longer. The guard is checked only when a delay or timer is made: a periodic timer already
    /// made keeps firing, and moving the time forward, past the guard until it is disposed or changed.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="guard"/> is zero or less.</exception>
    public InstantClock(TimeSpan? guard = null)
    {
        _guard = guard ?? _defaultGuard;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_guard, TimeSpan.Zero, nameof(guard));
    }

    /// <summary>The time zone the clock reports as local: always <see cref="TimeZoneInfo.Utc"/>.</summary>
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>The number of timestamp ticks in one second of the clock's time: <see cref="TimeSpan.TicksPerSecond"/>.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>The clock's current time: its fixed starting instant plus all the time its timers have moved it forward.</summary>
    /// <returns>The current time, in UTC.</returns>
    public override DateTimeOffset GetUtcNow() => _start + Elapsed;

    /// <summary>
    /// A timestamp of the clock's current time, in ticks of <see cref="TimestampFrequency"/>. It counts the same time as
    /// <see cref="GetUtcNow"/>.
    /// </summary>
    /// <returns>The current timestamp.</returns>
    public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);

    /// <summary>
    /// Creates a timer that fires as soon as the thread pool runs it once <paramref name="dueTime"/> is not infinite, moving the
    /// clock's time forward by <paramref name="dueTime"/> first.
    /// </summary>
    /// <param name="callback">Called each time the timer fires, never inside this call.</param>
    /// <param name="state">Passed to <paramref name="callback"/>.</param>
    /// <param name="dueTime">The fake time until the first firing. <see cref="Timeout.InfiniteTimeSpan"/> makes a timer that never fires until changed.</param>
    /// <param name="period">
    /// The fake time between later firings. <see cref="Timeout.InfiniteTimeSpan"/> or zero makes a timer that fires once.
    /// </param>
    /// <returns>
    /// The timer. Disposing it stops any firing not yet begun. Its <see cref="ITimer.Change"/> validates its arguments as this
    /// method does.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dueTime"/> or <paramref name="period"/> is negative other than <see cref="Timeout.InfiniteTimeSpan"/>, or
    /// more than <see cref="uint.MaxValue"/> - 1 milliseconds, the same range <see cref="TimeProvider.System"/> accepts. Time
    /// does not move.
    /// </exception>
    /// <exception cref="ClockGuardException">More fake time than the clock's guard has passed since the clock was made.</exception>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ThrowIfOutOfRange(dueTime, period);
        ThrowIfPastGuard();
        var timer = new InstantTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <exception cref="ClockGuardException">More fake time than the clock's guard has passed since the clock was made.</exception>
    internal void ThrowIfPastGuard()
    {
        var elapsed = Elapsed;
        if (elapsed > _guard)
        {
            throw new ClockGuardException(elapsed, _guard);
        }
    }

    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dueTime"/> or <paramref name="period"/> is outside the range <see cref="TimeProvider.System"/> accepts.
    /// </exception>
    internal static void ThrowIfOutOfRange(TimeSpan dueTime, TimeSpan period)
    {
        ThrowIfOutOfRange(dueTime, nameof(dueTime));
        ThrowIfOutOfRange(period, nameof(period));
    }

    private static void ThrowIfOutOfRange(TimeSpan time, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, Timeout.InfiniteTimeSpan, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(time, _maxTime, name);
    }

    private TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    private void MoveForward(TimeSpan by) => Interlocked.Add(ref _elapsedTicks, by.Ticks);

    private sealed class InstantTimer(InstantClock clock, TimerCallback callback, object? state) : ITimer
    {
        private readonly Lock _gate = new();
        private long _schedule;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ThrowIfOutOfRange(dueTime, period);
            long schedule;
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                schedule = ++_schedule;
            }

            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Queue(schedule, dueTime, period);
            }

            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _schedule++;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void Queue(long schedule, TimeSpan delay, TimeSpan period) =>
            ThreadPool.QueueUserWorkItem(_ => Fire(schedule, delay, period));

        private void Fire(long schedule, TimeSpan delay, TimeSpan period)
        {
            lock (_gate)
            {
                if (schedule != _schedule)
                {
                    return;
                }

                clock.MoveForward(delay);
            }

            callback(state);
            if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
            {
                Queue(schedule, period, period);
            }
        }
    }
}
