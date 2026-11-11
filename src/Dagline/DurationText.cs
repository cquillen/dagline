using System.Globalization;

namespace Dagline;

/// <summary>The one home of how a duration is written for people.</summary>
internal static class DurationText
{
    /// <summary>Seconds with one decimal, as in <c>3.1s</c>, rounded down.</summary>
    public static string Of(TimeSpan duration)
    {
        var tenths = duration.Ticks / (TimeSpan.TicksPerSecond / 10);
        return string.Create(CultureInfo.InvariantCulture, $"{tenths / 10}.{tenths % 10}s");
    }
}
