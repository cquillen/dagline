namespace Dagline.Testing;

/// <summary>
/// The clock of a scripted run that owns its clock: its time moves, and its timers fire, only when the test or the harness says,
/// on the calling thread, one timer at a time in the order they come due, each moving the time on to when it is due.
/// </summary>
/// <remarks>
/// <para>
/// It starts at midnight UTC on 1 January 2000, as an <see cref="InstantClock"/> does, and its local time zone is UTC. Its
/// timestamps count the same time as <see cref="GetUtcNow"/>. A timer with an infinite due time is never due until changed. A
/// periodic timer is due again one period after each firing; a period of zero or infinite makes a timer that fires once.
/// </para>
/// <para>
/// It is what a manual host needs in place of an <see cref="InstantClock"/>, whose timers fire on the thread pool as soon as they
/// are made, where a pump can neither see nor hold them back. Setting a timer rings the doorbell, so that a pump waiting in real
/// time for work it cannot see wakes when code off the pump sets one.
/// </para>
/// <para>It is safe to use from several threads at once.</para>
/// </remarks>
internal sealed class ScriptedClock : TimeProvider
{
    private static readonly DateTimeOffset _start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly List<ScriptedTimer> _timers = [];
    private readonly Action? _beforeEachTimer;
    private TimeSpan _elapsed;
    private long _sequence;

    /// <param name="beforeEachTimer">Called on entry to each <see cref="CreateTimer"/>, where it may refuse the timer by throwing.</param>
    public ScriptedClock(Action? beforeEachTimer = null)
    {
        _beforeEachTimer = beforeEachTimer;
    }

    /// <summary>Rung each time a timer is set to come due.</summary>
    public Doorbell Doorbell { get; } = new();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>The time the clock has moved since it was made.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _elapsed;
            }
        }
    }

    /// <summary>Whether any timer is set to come due.</summary>
    public bool HasTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count > 0;
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => _start + Elapsed;

    public override long GetTimestamp() => Elapsed.Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        InstantClock.ThrowIfOutOfRange(dueTime, period);
        _beforeEachTimer?.Invoke();
        var timer = new ScriptedTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Fires the timer due first, on the calling thread, once the time has moved on to when it is due, if it is due no later than
    /// <paramref name="latest"/>. Of two timers due at once, the one set first fires first.
    /// </summary>
    /// <returns><see langword="false"/> when no timer is due by then.</returns>
    public bool FireNext(TimeSpan latest)
    {
        ScriptedTimer next;
        lock (_gate)
        {
            if (_timers.Count == 0)
            {
                return false;
            }

            next = _timers.MinBy(timer => (timer.Due, timer.Sequence))!;
            if (next.Due > latest)
            {
                return false;
            }

            _timers.Remove(next);
            if (next.Due > _elapsed)
            {
                _elapsed = next.Due;
            }

            if (next.IsPeriodic)
            {
                Schedule(next, next.Due + next.Period);
            }
        }

        next.Fire();
        return true;
    }

    /// <summary>Moves the time on to <paramref name="elapsed"/> since the clock was made, firing nothing. Time never moves back.</summary>
    public void MoveTo(TimeSpan elapsed)
    {
        lock (_gate)
        {
            if (elapsed > _elapsed)
            {
                _elapsed = elapsed;
            }
        }
    }

    private void Schedule(ScriptedTimer timer, TimeSpan due)
    {
        timer.Due = due;
        timer.Sequence = _sequence++;
        _timers.Add(timer);
    }

    private sealed class ScriptedTimer(ScriptedClock clock, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;

        public TimeSpan Due { get; set; }

        public long Sequence { get; set; }

        public TimeSpan Period { get; private set; }

        public bool IsPeriodic => Period != Timeout.InfiniteTimeSpan && Period != TimeSpan.Zero;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            InstantClock.ThrowIfOutOfRange(dueTime, period);
            lock (clock._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                clock._timers.Remove(this);
                Period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                clock.Schedule(this, clock._elapsed + dueTime);
            }

            clock.Doorbell.Ring();
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                _disposed = true;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
