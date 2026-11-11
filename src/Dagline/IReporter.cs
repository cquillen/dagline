namespace Dagline;

/// <summary>
/// Something that presents a run to people while it happens. Implement this to watch a run,
/// and give the reporter to the run in <see cref="RunOptions.Reporters"/>.
/// </summary>
/// <remarks>
/// <para>
/// A reporter watches a run and cannot change it. Nothing a reporter does, or fails to do,
/// changes a task or the run outcome.
/// </para>
/// <para>
/// Each reporter has an unbounded queue of its own, filled as the run publishes events and emptied by calls to
/// <see cref="OnEventAsync"/>. The run never waits for a reporter while tasks are running: one that is slow falls behind,
/// and neither the run nor any other reporter is held up by it.
/// </para>
/// </remarks>
public interface IReporter
{
    /// <summary>Handles one event of the run.</summary>
    /// <param name="runEvent">The event. <see cref="RunStarted"/> is the first of a run and <see cref="RunEnded"/> the last.</param>
    /// <param name="snapshot">
    /// The run as of this event: it includes this event and none after it, however far the run has moved on since.
    /// The run as it is now is <see cref="ISnapshotSource.Current"/> on the source that <see cref="RunStarted"/> carries.
    /// </param>
    /// <param name="cancellationToken">
    /// A token for the work of handling the event. Each reporter is passed a token of its own. It is cancelled only when a request on the run's <see cref="StopSignal"/>
    /// ends the wait for reporters at the end of the run while this reporter is still busy. The reporter is then dropped.
    /// </param>
    /// <returns>A task that completes when the reporter has handled the event.</returns>
    /// <remarks>
    /// <para>
    /// Every reporter receives the same events in the same order, which is the order they happened in.
    /// A reporter is never called for an event while it is still handling an earlier one: the next call is made
    /// only once the task returned by this one has completed. A reporter therefore needs no lock for what it does
    /// with events. Calls are made from the thread pool, and not always from the same thread.
    /// </para>
    /// <para>
    /// A reporter that throws from this method, or returns a task that faults, is dropped: it receives no further call,
    /// the run and the other reporters carry on unaffected, and a <see cref="ReporterError"/> naming the reporter,
    /// the event and the exception is on <see cref="RunResult.ReporterErrors"/>. The exception is never thrown to the app.
    /// </para>
    /// <para>
    /// At its end the run waits, with no time bound, until every reporter that was not dropped has handled every event,
    /// <see cref="RunEnded"/> included. A call that never completes keeps <see cref="Runner.RunAsync"/> from returning
    /// until a request on the run's <see cref="StopSignal"/> that the run did not apply ends that wait. Each reporter still busy
    /// is then dropped: the token passed to its call is cancelled, its remaining events are discarded, <see cref="Runner.RunAsync"/>
    /// returns without waiting for the call to come back, even a call that blocks its thread before it returns a task, and its <see cref="ReporterError"/> on <see cref="RunResult.ReporterErrors"/>
    /// carries an <see cref="OperationCanceledException"/>. A run given no signal waits for its reporters for as long as they take.
    /// </para>
    /// <para>
    /// A reporter that appears twice in <see cref="RunOptions.Reporters"/> is treated as two reporters:
    /// it receives every event twice, and the two sets of calls can overlap.
    /// </para>
    /// </remarks>
    ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken);
}
