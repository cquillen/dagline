namespace Dagline;

/// <summary>
/// The one home of what a grace period means: a wait on the run's clock, allowed as <see cref="ClockWaitRules"/> says.
/// </summary>
internal static class GracePeriodRules
{
    /// <summary>The problem with a grace period given in the options, or <see langword="null"/> when it is allowed.</summary>
    public static string? ProblemWith(TimeSpan gracePeriod) => ClockWaitRules.ProblemWith("The grace period", gracePeriod);

    /// <summary><see langword="true"/> when the run waits for its running tasks with no limit.</summary>
    public static bool IsUnlimited(TimeSpan gracePeriod) => ClockWaitRules.IsUnlimited(gracePeriod);

    /// <summary>When a wait that begins at <paramref name="now"/> ends at the latest, or <see langword="null"/> when it has no limit.</summary>
    public static DateTimeOffset? DeadlineFrom(DateTimeOffset now, TimeSpan gracePeriod)
        => IsUnlimited(gracePeriod) ? null : now + gracePeriod;
}
