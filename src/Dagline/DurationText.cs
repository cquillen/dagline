using static System.FormattableString;

namespace Dagline;

/// <summary>The one home of how a duration is written for people.</summary>
internal static class DurationText
{
    private const long _ticksPerTenth = TimeSpan.TicksPerSecond / 10;

    /// <summary>
    /// Seconds with one decimal under a minute, as in <c>3.1s</c>. Minutes and seconds under an hour, as in <c>1m15s</c>.
    /// Hours and minutes from there, as in <c>2h14m</c>, with the hours never folded into days. Always rounded down.
    /// A duration below zero, which a clock that was set back can give, is written as zero.
    /// </summary>
    public static string Of(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration < TimeSpan.FromMinutes(1))
        {
            var tenths = duration.Ticks / _ticksPerTenth;
            return Invariant($"{tenths / 10}.{tenths % 10}s");
        }

        return duration < TimeSpan.FromHours(1)
            ? Invariant($"{duration.Minutes}m{duration.Seconds}s")
            : Invariant($"{(long)duration.TotalHours}h{duration.Minutes}m");
    }
}
