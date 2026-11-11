namespace Dagline.TestSupport;

/// <summary>
/// A reporter that hands every event on to another reporter and lets a test wait until that reporter is done with one.
/// A test of a reporter that works on its own clock, such as one that prints on a timer, awaits <see cref="Handled"/>
/// before it advances the clock, so that the reporter under test is never caught halfway through an event.
/// </summary>
/// <param name="watched">The reporter under test. It is called exactly as the run would call it.</param>
public sealed class WatchedReporter(IReporter watched) : IReporter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _handled = [];

    /// <summary>
    /// Completes once the watched reporter has returned from, or thrown from, the call for the event that
    /// <see cref="EventLine.Describe"/> writes as <paramref name="line"/>. It may be asked for before or after that call.
    /// </summary>
    /// <param name="line">The event as one line, such as <c>prepare: ready -> running</c>.</param>
    public Task Handled(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return SignalFor(line).Task;
    }

    /// <inheritdoc />
    /// <remarks>An exception of the watched reporter is thrown on, so the run drops this reporter as it would have dropped that one.</remarks>
    public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await watched.OnEventAsync(runEvent, snapshot, cancellationToken);
        }
        finally
        {
            SignalFor(EventLine.Describe(runEvent)).TrySetResult();
        }
    }

    private TaskCompletionSource SignalFor(string line)
    {
        lock (_gate)
        {
            if (!_handled.TryGetValue(line, out var signal))
            {
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _handled.Add(line, signal);
            }

            return signal;
        }
    }
}
