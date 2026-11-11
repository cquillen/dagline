using static System.FormattableString;

namespace Dagline;

/// <summary>
/// The one home of what a grace period may be and what it means: from zero up to <see cref="Maximum"/>,
/// or <see cref="Timeout.InfiniteTimeSpan"/> for a wait with no limit.
/// </summary>
internal static class GracePeriodRules
{
    /// <summary>
    /// The longest limited grace period: the longest timer a <see cref="TimeProvider"/> can create,
    /// <see cref="uint.MaxValue"/> - 1 milliseconds, about 49.7 days.
    /// </summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>The problem with a grace period given in the options, or <see langword="null"/> when it is allowed.</summary>
    public static string? ProblemWith(TimeSpan gracePeriod)
        => IsUnlimited(gracePeriod) ? null
            : gracePeriod < TimeSpan.Zero
                ? Invariant($"The grace period is {gracePeriod}. It must be zero or more, or Timeout.InfiniteTimeSpan to wait with no limit.")
            : gracePeriod > Maximum
                ? Invariant($"The grace period is {gracePeriod}. It must be at most {Maximum}, about 49.7 days, or Timeout.InfiniteTimeSpan to wait with no limit.")
            : null;

    /// <summary><see langword="true"/> when the run waits for its running tasks with no limit.</summary>
    public static bool IsUnlimited(TimeSpan gracePeriod) => gracePeriod == Timeout.InfiniteTimeSpan;

    /// <summary>When a wait that begins at <paramref name="now"/> ends at the latest, or <see langword="null"/> when it has no limit.</summary>
    public static DateTimeOffset? DeadlineFrom(DateTimeOffset now, TimeSpan gracePeriod)
        => IsUnlimited(gracePeriod) ? null : now + gracePeriod;
}
