using System.Globalization;
using static System.FormattableString;

namespace Dagline.Spectre;

/// <summary>The one home of how the display and the summary write numbers and durations.</summary>
internal static class DisplayText
{
    /// <summary>A whole number with its thousands grouped, as in <c>3,173</c>, in the invariant culture.</summary>
    public static string Number(long number) => number.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whole seconds under a minute, as in <c>45s</c>. Minutes and seconds under an hour, as in <c>20m 44s</c>. Hours and minutes
    /// from there, as in <c>1h 15m</c>. Always rounded down, and a duration below zero is written as zero.
    /// </summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration < TimeSpan.FromMinutes(1))
        {
            return Invariant($"{duration.Seconds}s");
        }

        return duration < TimeSpan.FromHours(1)
            ? Invariant($"{duration.Minutes}m {duration.Seconds}s")
            : Invariant($"{(long)duration.TotalHours}h {duration.Minutes}m");
    }
}
