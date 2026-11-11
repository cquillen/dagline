using static System.FormattableString;

namespace Dagline;

/// <summary>
/// The one home of what a grace period may be and what it means: zero or more, or <see cref="Timeout.InfiniteTimeSpan"/>
/// for a wait with no limit.
/// </summary>
internal static class GracePeriodRules
{
    /// <summary>The problem with a grace period given in the options, or <see langword="null"/> when it is allowed.</summary>
    public static string? ProblemWith(TimeSpan gracePeriod)
        => gracePeriod >= TimeSpan.Zero || IsUnlimited(gracePeriod)
            ? null
            : Invariant($"The grace period is {gracePeriod}. It must be zero or more, or Timeout.InfiniteTimeSpan to wait with no limit.");

    /// <summary><see langword="true"/> when the run waits for its running tasks with no limit.</summary>
    public static bool IsUnlimited(TimeSpan gracePeriod) => gracePeriod == Timeout.InfiniteTimeSpan;

    /// <summary>When a wait that begins at <paramref name="now"/> ends at the latest, or <see langword="null"/> when it has no limit.</summary>
    public static DateTimeOffset? DeadlineFrom(DateTimeOffset now, TimeSpan gracePeriod)
        => IsUnlimited(gracePeriod) ? null : now + gracePeriod;
}
