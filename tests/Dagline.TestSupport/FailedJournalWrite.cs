using System.Reflection;

namespace Dagline.TestSupport;

/// <summary>
/// The two events a failed journal write gives, for a test of how a reporter shows them.
/// </summary>
/// <remarks>
/// No real file can be made to fail on every platform, so no real run gives these, and only Dagline can construct an event.
/// This is the one place a test builds a type that only Dagline creates: it calls the event's internal constructor by reflection,
/// as <c>CODING_STANDARDS.md</c> allows for this path alone. Every other event a test asserts on comes from a real run.
/// </remarks>
public static class FailedJournalWrite
{
    /// <summary>The stop Dagline applies when a journal write fails.</summary>
    /// <param name="at">When the stop was applied.</param>
    /// <param name="gracePeriod">How long the run waits for its running tasks to end.</param>
    public static StopRequested StopRequested(DateTimeOffset at, TimeSpan gracePeriod)
        => Construct<StopRequested>(at, StopRequester.Dagline, gracePeriod);

    /// <summary>The end of a run whose journal could no longer be written: an error in place of a run outcome.</summary>
    /// <param name="at">When the run ended.</param>
    /// <param name="error">The error the run ended with.</param>
    public static RunEnded RunEnded(DateTimeOffset at, Exception error) => Construct<RunEnded>(at, error);

    private static T Construct<T>(params object[] arguments)
    {
        var constructor = typeof(T).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [.. arguments.Select(argument => argument is Exception ? typeof(Exception) : argument.GetType())])
            ?? throw new InvalidOperationException($"{typeof(T).Name} has no such internal constructor.");
        return (T)constructor.Invoke(arguments);
    }
}
