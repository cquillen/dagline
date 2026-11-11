namespace Dagline.TestSupport;

/// <summary>
/// A reporter that hands every event on to another reporter and lets a test wait until that reporter is done with one.
/// A test of a reporter that works on its own clock, such as one that prints on a timer, awaits <see cref="Handled"/>
/// before it advances the clock, so that the reporter under test is never caught halfway through an event.
/// </summary>
public sealed class WatchedReporter : IReporter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _handled = [];
    private readonly IReporter _watched;

    /// <summary>Makes a reporter that watches another.</summary>
    /// <param name="watched">The reporter under test. It is called exactly as the run would call it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="watched"/> is <see langword="null"/>.</exception>
    public WatchedReporter(IReporter watched)
    {
        ArgumentNullException.ThrowIfNull(watched);
        _watched = watched;
    }

    /// <summary>
    /// Completes once the watched reporter has returned from, or thrown from, the call for the event that
    /// <see cref="EventLine.Describe"/> writes as <paramref name="line"/>. It may be asked for before or after that call.
    /// </summary>
    /// <param name="line">The event as one line, such as <c>prepare: ready -> running</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public Task Handled(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return SignalFor(line).Task;
    }

    /// <inheritdoc />
    /// <remarks>An exception of the watched reporter is thrown on, so the run drops this reporter as it would have dropped that one.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="runEvent"/> or <paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            await _watched.OnEventAsync(runEvent, snapshot, cancellationToken);
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
