using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.TestSupport;

/// <summary>
/// A run's clock that tells the test each time a timer is created on it, so that the test can advance the
/// <see cref="FakeTimeProvider"/> beneath it by exactly a delay once that delay is pending. Give it to the run as
/// <see cref="RunOptions.TimeProvider"/>, and advance the fake clock it wraps.
/// </summary>
/// <remarks>
/// <see cref="FakeTimeProvider"/> cannot say when a timer is pending. A test that advances it before a delay has begun moves
/// the start of that delay too, so the time between two checks would come out as more than one interval.
/// </remarks>
public sealed class WatchedClock : TimeProvider
{
    private readonly FakeTimeProvider _clock;
    private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();

    /// <summary>Wraps <paramref name="clock"/>, which keeps the time and fires the timers.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is <see langword="null"/>.</exception>
    public WatchedClock(FakeTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

    /// <inheritdoc />
    public override long GetTimestamp() => _clock.GetTimestamp();

    /// <inheritdoc />
    public override long TimestampFrequency => _clock.TimestampFrequency;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => _clock.LocalTimeZone;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _clock.CreateTimer(callback, state, dueTime, period);
        _timers.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>
    /// Completes once the next timer has been created on this clock, in the order created, with its due time.
    /// The timer is pending from then on, so advancing the fake clock by the due time fires it.
    /// </summary>
    public ValueTask<TimeSpan> NextTimerAsync() => _timers.Reader.ReadAsync();
}
