namespace Dagline;

/// <summary>
/// Something that presents a run to people while it happens. Implement this to watch a run,
/// and give the reporter to the run in <see cref="RunOptions.Reporters"/>.
/// </summary>
/// <remarks>A reporter watches a run and cannot change it.</remarks>
public interface IReporter
{
    /// <summary>Handles one event of the run.</summary>
    /// <param name="runEvent">The event. <see cref="RunStarted"/> is the first of a run and <see cref="RunEnded"/> the last.</param>
    /// <param name="snapshot">The run as of this event: it includes this event and none after it.</param>
    /// <param name="cancellationToken">A token for the work of handling the event. It is never cancelled.</param>
    /// <returns>A task that completes when the reporter has handled the event.</returns>
    /// <remarks>
    /// Events arrive in the order they happened, and a reporter is never called for an event while it is still handling an earlier one.
    /// Calls are made from the thread pool, and the run ends only once every event has been handled.
    /// </remarks>
    ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken);
}
