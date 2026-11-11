namespace Dagline.TestSupport;

/// <summary>
/// Writes an event as one short line, so that a test can assert on a whole run's events as a list of strings.
/// Times are left out: a test that cares about them asserts on the events themselves.
/// </summary>
public static class EventLine
{
    /// <summary>
    /// <c>run started</c>, <c>prepare: pending -> ready</c> or <c>run ended: complete</c>.
    /// A kind of event this does not know is written with its own <c>ToString</c>.
    /// </summary>
    public static string Describe(RunEvent runEvent) => runEvent switch
    {
        RunStarted => "run started",
        TaskStatusChanged changed => $"{changed.Node.Path}: {Lower(changed.From)} -> {Lower(changed.To)}",
        RunEnded ended => $"run ended: {Lower(ended.Outcome)}",
        _ => runEvent.ToString(),
    };

    private static string Lower<T>(T value)
        where T : struct, Enum
        => value.ToString().ToLowerInvariant();
}
