namespace Dagline.Testing;

/// <summary>
/// Thrown by an <see cref="InstantClock"/> asked to make a delay or timer once more fake time than its guard has passed since it
/// was made. It stops a check that never returns <see langword="true"/> from spinning for ever.
/// </summary>
/// <remarks>
/// It is not a <see cref="DaglineException"/>: Dagline itself never throws it. Thrown inside a task body, as from a delay of
/// <see cref="TaskContext.WaitUntil"/>, it fails the task as an unexpected failure and the run goes on to its end.
/// </remarks>
public sealed class ClockGuardException : Exception
{
    internal ClockGuardException(TimeSpan elapsed, TimeSpan guard)
        : base($"The instant clock has passed {elapsed} of fake time, more than its guard of {guard}. A check may never be returning true.")
    {
    }
}
