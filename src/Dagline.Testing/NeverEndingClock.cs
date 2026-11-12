namespace Dagline.Testing;

/// <summary>
/// A clock on which no delay or timer ever ends and time stands still, so a task that waits is still waiting when a stop
/// arrives.
/// </summary>
internal sealed class NeverEndingClock : TimeProvider
{
    private static readonly DateTimeOffset _now = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => 0;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new NeverFiringTimer();
    }

    private sealed class NeverFiringTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
