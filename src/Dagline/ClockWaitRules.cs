using static System.FormattableString;

namespace Dagline;

/// <summary>
/// The one home of what a wait the run measures on its clock with a timer may be, such as the grace period and the reporter
/// wait: from zero up to <see cref="Maximum"/>, or <see cref="Timeout.InfiniteTimeSpan"/> for a wait with no limit.
/// </summary>
internal static class ClockWaitRules
{
    /// <summary>
    /// The longest limited wait: the longest timer a <see cref="TimeProvider"/> can create, <see cref="uint.MaxValue"/> - 1
    /// milliseconds, about 49.7 days.
    /// </summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// The problem with a wait given in the options, or <see langword="null"/> when it is allowed.
    /// </summary>
    /// <param name="name">The wait's name, as a sentence begins with it, such as "The grace period".</param>
    /// <param name="wait">The wait given.</param>
    public static string? ProblemWith(string name, TimeSpan wait)
        => IsUnlimited(wait) ? null
            : wait < TimeSpan.Zero
                ? Invariant($"{name} is {wait}. It must be zero or more, or Timeout.InfiniteTimeSpan to wait with no limit.")
            : wait > Maximum
                ? Invariant($"{name} is {wait}. It must be at most {Maximum}, about 49.7 days, or Timeout.InfiniteTimeSpan to wait with no limit.")
            : null;

    /// <summary><see langword="true"/> when the wait has no limit.</summary>
    public static bool IsUnlimited(TimeSpan wait) => wait == Timeout.InfiniteTimeSpan;
}
