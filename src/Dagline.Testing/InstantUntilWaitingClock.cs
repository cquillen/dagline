namespace Dagline.Testing;

/// <summary>
/// A clock on which a delay or timer ends at once, as on an <see cref="InstantClock"/>, while the task is not waiting, and
/// never fires while it is, so a task that delays before its wait reaches the wait and is still waiting when a stop arrives.
/// </summary>
/// <remarks>
/// Whether the task is waiting is decided when a timer is made, again on every change, and again before each repeat of a
/// periodic timer, so no timer fires while the task is waiting, whenever it was made; only a firing already under way
/// as the wait begins still runs. A timer decided while the task was waiting never fires, even after the wait ends. Time, its zone and the guard are the shared instant clock's.
/// </remarks>
internal sealed class InstantUntilWaitingClock(InstantClock time, Func<bool> isWaiting) : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone => time.LocalTimeZone;

    public override long TimestampFrequency => time.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => time.GetUtcNow();

    public override long GetTimestamp() => time.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        InstantClock.ThrowIfOutOfRange(dueTime, period);
        var timer = new UntilWaitingTimer(time, isWaiting, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    // Every firing of the instant timer underneath is one-shot: this timer schedules each repeat itself, so that it decides
    // again before each one. The waiting state is always read outside the gate, since the live snapshot takes the run's lock.
    private sealed class UntilWaitingTimer : ITimer
    {
        private readonly Lock _gate = new();
        private readonly Func<bool> _isWaiting;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly ITimer _instant;
        private TimeSpan _period;
        private long _schedule;
        private bool _repeating;
        private bool _disposed;

        public UntilWaitingTimer(InstantClock time, Func<bool> isWaiting, TimerCallback callback, object? state)
        {
            _isWaiting = isWaiting;
            _callback = callback;
            _state = state;
            _instant = time.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            InstantClock.ThrowIfOutOfRange(dueTime, period);
            var fires = dueTime != Timeout.InfiniteTimeSpan && !_isWaiting();
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _schedule++;
                _period = period;
                _repeating = false;
                return _instant.Change(fires ? dueTime : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _schedule++;
            }

            _instant.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void Fire()
        {
            long schedule;
            TimeSpan period;
            bool repeating;
            lock (_gate)
            {
                schedule = _schedule;
                period = _period;
                repeating = _repeating;
            }

            if (repeating && _isWaiting())
            {
                return;
            }

            _callback(_state);
            if (period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero || _isWaiting())
            {
                return;
            }

            lock (_gate)
            {
                if (schedule == _schedule && !_disposed)
                {
                    _repeating = true;
                    _instant.Change(period, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }
}
