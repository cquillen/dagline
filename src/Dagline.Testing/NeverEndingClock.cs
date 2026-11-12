namespace Dagline.Testing;

/// <summary>
/// A clock on which no delay or timer ever ends and time stands still, so a task that waits is still waiting when a stop
/// arrives.
/// </summary>
internal sealed class NeverEndingClock : TimeProvider
{
    // Only a timer made from an instant clock moves its time, and none is ever made from this one, so it keeps the instant
    // clock's starting time and zone for ever.
    private readonly InstantClock _time = new();

    public override TimeZoneInfo LocalTimeZone => _time.LocalTimeZone;

    public override long TimestampFrequency => _time.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();

    public override long GetTimestamp() => _time.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        InstantClock.ThrowIfOutOfRange(dueTime, period);
        return new NeverFiringTimer();
    }

    private sealed class NeverFiringTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            InstantClock.ThrowIfOutOfRange(dueTime, period);
            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
