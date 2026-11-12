namespace Dagline.TestSupport;

/// <summary>
/// Writes an event as one short line, so that a test can assert on a whole run's events as a list of strings.
/// Times are left out: a test that cares about them asserts on the events themselves.
/// </summary>
public static class EventLine
{
    /// <summary>
    /// <c>run started</c>, <c>prepare: pending -> ready</c>, <c>warning prepare: entity 9 has no region</c>,
    /// <c>waiting job: job 17 to finish</c> (or <c>waiting job</c> with no text), <c>waited job</c>,
    /// <c>group shard-1 settled: succeeded</c>, <c>stop requested</c>, <c>stopped waiting: second stop</c>,
    /// <c>stopped waiting: grace period over</c> or <c>run ended: complete</c>.
    /// A kind of event this does not know is written with its own <c>ToString</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="runEvent"/> is <see langword="null"/>.</exception>
    public static string Describe(RunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        return runEvent switch
        {
            RunStarted => "run started",
            TaskStatusChanged changed => $"{changed.Node.Path}: {Lower(changed.From)} -> {Lower(changed.To)}",
            WarningAdded added => $"warning {added.Path}: {added.Warning}",
            WaitingChanged { Waiting: true, Text: null } began => $"waiting {began.Path}",
            WaitingChanged { Waiting: true } began => $"waiting {began.Path}: {began.Text}",
            WaitingChanged ended => $"waited {ended.Path}",
            GroupSettled settled => $"group {settled.Path} settled: {Lower(settled.Status)}",
            StopRequested => "stop requested",
            StoppedWaiting { Reason: StoppedWaitingReason.SecondStop } => "stopped waiting: second stop",
            StoppedWaiting => "stopped waiting: grace period over",
            RunEnded { Outcome: { } outcome } => $"run ended: {Lower(outcome)}",
            RunEnded ended => $"run ended: {ended.Error!.GetType().Name}",
            _ => runEvent.ToString(),
        };
    }

    private static string Lower<T>(T value)
        where T : struct, Enum
        => value.ToString().ToLowerInvariant();
}
