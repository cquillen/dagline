namespace Dagline.Prototype;

/// <summary>
/// A clock whose timers fire only when the driver says so, on the driver's thread, one at a time in due order, each moving the
/// time on to its due time. It is what a manual host needs in place of Dagline.Testing's InstantClock, whose timers fire on the
/// thread pool as soon as they are made: a pump cannot see those, and cannot hold them back.
/// </summary>
internal sealed class PumpedClock : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<PumpedTimer> _timers = [];
    private DateTimeOffset _now = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _sequence;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new PumpedTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Whether any timer is waiting to fire.</summary>
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

    /// <summary>
    /// Fires the timer due first, on the calling thread, after moving the time on to when it is due.
    /// </summary>
    /// <returns><see langword="false"/> when no timer is waiting.</returns>
    public bool FireNext()
    {
        PumpedTimer next;
        lock (_gate)
        {
            if (_timers.Count == 0)
            {
                return false;
            }

            next = _timers.MinBy(timer => (timer.Due, timer.Sequence))!;
            _timers.Remove(next);
            if (next.Due > _now)
            {
                _now = next.Due;
            }
        }

        next.Fire();
        return true;
    }

    private sealed class PumpedTimer(PumpedClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        public long Sequence { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = clock._now + dueTime;
                    Sequence = clock._sequence++;
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
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
