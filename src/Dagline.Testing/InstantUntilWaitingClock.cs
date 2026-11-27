namespace Dagline.Testing;

/// <summary>
/// A clock on which a delay or timer ends at once, as on an <see cref="InstantClock"/>, while the task is not waiting, and
/// never fires while it is, so a task that delays before its wait reaches the wait and is still waiting when a stop arrives.
/// </summary>
/// <remarks>
/// One clock serves one run, and asks that run whether the task is waiting. It asks when a timer is made, again on every
/// change and before each repeat of a periodic timer, and again just before every callback. A timer decided while the task
/// was waiting never fires, even after the wait ends, and a firing that finds the task waiting runs no callback, so a
/// callback runs while the task waits only if its firing had already found the task not waiting when the wait began. Time,
/// its zone and the guard are the shared instant clock's.
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
        timer.Arm(dueTime, period);
        return timer;
    }

    // Every firing of the instant timer underneath is one-shot: this timer schedules each repeat itself, so that it decides
    // again before each one. The waiting state is read outside the gate, since the live snapshot takes the run's lock, so each
    // decision holds a ticket taken under the gate and applies only if no later change or disposal has taken a newer one, and a
    // firing calls back only if the ticket that armed it is still the latest.
    private sealed class UntilWaitingTimer : ITimer
    {
        private readonly Lock _gate = new();
        private readonly Func<bool> _isWaiting;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly ITimer _instant;
        private TimeSpan _period;
        private long _ticket;
        private long _armed;
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
            return Arm(dueTime, period);
        }

        public bool Arm(TimeSpan dueTime, TimeSpan period)
        {
            long ticket;
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                ticket = ++_ticket;
                _period = period;
            }

            Schedule(ticket, dueTime);
            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _ticket++;
            }

            _instant.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void Schedule(long ticket, TimeSpan dueTime)
        {
            var due = dueTime != Timeout.InfiniteTimeSpan && !_isWaiting() ? dueTime : Timeout.InfiniteTimeSpan;
            lock (_gate)
            {
                if (ticket == _ticket)
                {
                    _armed = ticket;
                    _instant.Change(due, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void Fire()
        {
            long ticket;
            TimeSpan period;
            lock (_gate)
            {
                if (_armed != _ticket)
                {
                    return;
                }

                ticket = _armed;
                period = _period;
            }

            // The firing was decided when it was scheduled, but the task may have begun waiting since.
            if (_isWaiting())
            {
                return;
            }

            _callback(_state);
            if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
            {
                Schedule(ticket, period);
            }
        }
    }
}
