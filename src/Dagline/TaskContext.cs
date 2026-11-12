using System.Text;

namespace Dagline;

/// <summary>
/// What a run gives a task body: the task's identity, the run's clock, and the means for the task to say
/// what it is doing and what it found.
/// </summary>
/// <remarks>
/// <para>
/// A run creates one context for each task it starts, so a graph that is run twice gives each run's bodies their own contexts.
/// </para>
/// <para>
/// A task reports three kinds of thing. <em>Progress</em> is <see cref="Status"/> and <see cref="Count"/>: it shows in the snapshot
/// while the task runs, is never an event, and is frozen as the task left it when the task settles. <em>Warnings</em>, added with
/// <see cref="Warn"/>, show as they are added and stay on the task whatever status it settles with. The <em>outcome</em> is
/// the message set with <see cref="Message"/> and the detail lines added with <see cref="Detail"/>, on a task that succeeds
/// as much as on one that fails. <see cref="Fail"/> ends the task as a reported failure.
/// </para>
/// <para>
/// Everything reported shows in <see cref="ISnapshotSource.Current"/> from the moment the call returns, on the task's
/// <see cref="NodeSnapshot"/>, while the task is still running. A warning is the only report that is also an event.
/// </para>
/// <para>
/// A context is safe to call from several threads at once, so a body that fans out needs no lock of its own. Each call is applied
/// whole, one at a time, in the same single order as the run's changes of status, and no report is lost.
/// Calls made at once from several threads are applied in an order that is not defined.
/// </para>
/// <para>
/// Once the task has settled, <see cref="Status"/>, <see cref="Count"/>, <see cref="Warn"/>, <see cref="Message"/>,
/// <see cref="Detail"/> and <see cref="Fail"/> change nothing and publish nothing, though they still check their arguments.
/// <see cref="StatusOf"/> and <see cref="Checkpoint"/> still answer. <see cref="SaveCheckpointAsync"/> and
/// <see cref="ClearCheckpointAsync"/> are the exception: they throw <see cref="InvalidOperationException"/>, so that a task never
/// believes a checkpoint was kept when it was not. A body sees this only when it leaves work running after it has ended.
/// </para>
/// <para>
/// The same holds for a task the run has <em>let go of</em>: one still running when the run stopped waiting for it after a stop
/// (<see cref="RunOptions.GracePeriod"/>). The run records it as <see cref="NodeStatus.Cancelled"/> with the message
/// <c>still running when the run stopped waiting</c>, in place of any message it set, and keeps its warnings, detail lines and
/// frozen progress. Its body may still be running, and nothing it does changes the run: the reporting members do nothing
/// and do not throw, <see cref="Waiting"/> returns a scope that does nothing, and disposing a scope does nothing.
/// A save or a clearing of the checkpoint throws <see cref="InvalidOperationException"/>, as on a task that has settled.
/// <see cref="StatusOf"/>, <see cref="Checkpoint"/>, <see cref="Path"/>, <see cref="TimeProvider"/> and <see cref="CancellationToken"/> keep working.
/// When the body ends, however it ends, nothing is published and no snapshot changes, and an exception from it is dropped.
/// </para>
/// <para>
/// Dagline keeps the first 100 warnings and the first 100 detail lines of a task, counted separately, and only counts the rest,
/// in <see cref="NodeSnapshot.WarningsOmitted"/> and <see cref="NodeSnapshot.DetailOmitted"/>. A task that finds three thousand
/// problems therefore does not flood the run: report the count in the message and the first few in the detail.
/// </para>
/// </remarks>
public sealed class TaskContext
{
    private const int _checkpointBytes = 4096;

    private readonly Run _run;
    private readonly BuiltTask _task;

    internal TaskContext(Run run, BuiltTask task, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _run = run;
        _task = task;
        TimeProvider = timeProvider;
        CancellationToken = cancellationToken;
    }

    /// <summary>The path of the task this body is running as.</summary>
    public NodePath Path => _task.Path;

    /// <summary>
    /// The clock of the run, as given in <see cref="RunOptions.TimeProvider"/>.
    /// Take any delay or timestamp of the task's own from it, so that a test of the task controls time.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// The run's cancellation token, signalled when the run is stopped. Pass it to everything the body awaits, so that the task
    /// ends promptly once a stop has been requested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every task of a run sees the same token. It is signalled once, when the run applies a stop, and stays signalled.
    /// A run that is never stopped never signals it. The token reads as signalled from the step that applies the stop, and so before
    /// <see cref="Runner.RunAsync"/> returns. The callbacks registered on it run afterwards on the thread pool, never while the run
    /// holds its lock, so a callback may call this context, and the run does not wait for them: it may return while one is still running.
    /// </para>
    /// <para>
    /// Once a stop has been requested, a task ends as follows. A failure recorded with <see cref="Fail"/> wins: the task fails
    /// as a reported failure. Otherwise a body that ends with any <see cref="OperationCanceledException"/>, whichever token caused it,
    /// is <see cref="NodeStatus.Cancelled"/>, and keeps its message, detail lines, warnings and frozen progress. Otherwise the task
    /// gets its real status: succeeded when the body returned, and failed as an unexpected failure when it threw anything else.
    /// Before any stop a cancellation exception is an unexpected failure, like any other exception.
    /// </para>
    /// <para>
    /// A cancelled task never blocks: a task that requires it stays pending, and a task that runs after it becomes ready.
    /// Neither starts, since nothing starts once a stop has been requested.
    /// </para>
    /// </remarks>
    public CancellationToken CancellationToken { get; }

    /// <summary>Sets the status text of the task's progress: a line that says what the task is doing now.</summary>
    /// <param name="text">The new status text, which replaces the last one. <see langword="null"/> clears it.</param>
    /// <remarks>
    /// The text shows as <see cref="NodeSnapshot.StatusText"/>. It is independent of the count: setting one leaves the other as it was.
    /// Progress is never an event, so calling this often costs a reporter nothing. The text is kept as given, line breaks included.
    /// A reporter decides how to show them: <see cref="PlainTextReporter"/> prints each as a space.
    /// </remarks>
    public void Status(string? text) => _run.SetStatusText(_task, text);

    /// <summary>Sets the count of the task's progress, with or without the total it is counting towards.</summary>
    /// <param name="current">How far the task has got. It replaces the last count.</param>
    /// <param name="total">
    /// What the count is counting towards, or <see langword="null"/> for a running tally. It replaces the last total,
    /// so a call without one clears it.
    /// </param>
    /// <remarks>
    /// The numbers show as <see cref="NodeSnapshot.Count"/> and <see cref="NodeSnapshot.Total"/>. Dagline does not compare them,
    /// so a count above its total is kept as given. Progress is never an event.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="current"/> or <paramref name="total"/> is negative. Nothing is changed.
    /// </exception>
    public void Count(long current, long? total = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        if (total is { } towards)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(towards, nameof(total));
        }

        _run.SetCount(_task, current, total);
    }

    /// <summary>
    /// Adds a warning to the task's outcome: a one-line note that never changes the task's status and never blocks a dependent.
    /// </summary>
    /// <param name="warning">The warning. Each line break in it is replaced by a space, so it is one line.</param>
    /// <remarks>
    /// <para>
    /// A warning that is kept is added to <see cref="NodeSnapshot.Warnings"/> and published to every reporter
    /// as a <see cref="WarningAdded"/> event, which is a step of its own: a warning added before the body ends
    /// precedes the event of the task settling. It stays on the task whether the task succeeds or fails.
    /// </para>
    /// <para>
    /// Only the first 100 warnings of a task are kept. A later one is counted in <see cref="NodeSnapshot.WarningsOmitted"/>
    /// and no event is published for it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="warning"/> is <see langword="null"/>.</exception>
    public void Warn(string warning)
    {
        ArgumentNullException.ThrowIfNull(warning);

        _run.Warn(_task, warning);
    }

    /// <summary>Sets the one-line message of the task's outcome: what the task did, or what it found.</summary>
    /// <param name="message">The new message, which replaces the last one. Each line break in it is replaced by a space.</param>
    /// <remarks>
    /// The message shows as <see cref="NodeSnapshot.Message"/> and stays when the task succeeds, so a succeeded task can say
    /// what it did. <see cref="Fail"/> replaces it, and so does an exception that escapes the body when no failure was reported.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public void Message(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _run.SetMessage(_task, message);
    }

    /// <summary>Adds detail lines to the task's outcome, after those already there.</summary>
    /// <param name="lines">
    /// The lines to add, in order. Text that holds line breaks is split into one line for each break, so a multi-line text
    /// can be passed whole. An empty line is kept.
    /// </param>
    /// <remarks>
    /// The lines show in <see cref="NodeSnapshot.Detail"/> and stay whatever status the task settles with.
    /// Only the first 100 detail lines of a task are kept, counted after splitting. Later ones are counted in
    /// <see cref="NodeSnapshot.DetailOmitted"/>. The lines given to <see cref="Fail"/> and the type and stack trace
    /// of an unexpected failure count towards the same 100.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A line in <paramref name="lines"/> is <see langword="null"/>. Nothing is added.</exception>
    public void Detail(params string[] lines)
    {
        ThrowIfAnyNull(lines, nameof(lines));

        _run.AddDetail(_task, lines);
    }

    /// <summary>
    /// Records a reported failure, a failure the task declares itself, and returns the exception for the body to throw:
    /// <c>throw context.Fail("2 bad entities", "entity 3: missing owner");</c>.
    /// </summary>
    /// <param name="message">
    /// The message of the failure, which replaces the task's message. Each line break in it is replaced by a space.
    /// </param>
    /// <param name="detail">
    /// Detail lines to add after those the task already has, as <see cref="Detail"/> adds them: text that holds line breaks
    /// is split into one line for each, and the lines count towards the 100 kept.
    /// </param>
    /// <returns>
    /// An exception that carries the message and the detail lines of this call. Throw it to leave the body.
    /// A call made after the task has settled returns one too, and records nothing.
    /// </returns>
    /// <remarks>
    /// <para>
    /// It is the call that records the failure, not the exception. Once this method has been called the task settles
    /// <see cref="NodeStatus.Failed"/> with <see cref="FailureKind.Reported"/> however the body ends: by throwing the exception
    /// returned, by returning normally, or by letting a different exception escape, which is then not recorded.
    /// A forgotten <see langword="throw"/> therefore never turns a failure into a success, though the body runs on
    /// to its end. Like any failure, a reported failure blocks every task that requires this one.
    /// </para>
    /// <para>
    /// Calling it again replaces the message and adds more detail lines. <see cref="Message"/> and <see cref="Detail"/>
    /// called afterwards still apply.
    /// </para>
    /// <para>
    /// Throwing a <see cref="ReportedFailureException"/> does not itself make a failure a reported one. A task whose own context
    /// recorded no failure, and which lets one escape that another task's <see cref="Fail"/> returned, ends as an unexpected failure.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="detail"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A line in <paramref name="detail"/> is <see langword="null"/>. No failure is recorded.</exception>
    public ReportedFailureException Fail(string message, params string[] detail)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfAnyNull(detail, nameof(detail));

        _run.Fail(_task, message, detail);

        return new ReportedFailureException(LineBreaks.OneLine(message), [.. detail.SelectMany(LineBreaks.Lines)]);
    }

    /// <summary>
    /// Opens a waiting scope: marks the task as idle until an external system changes, until the scope is disposed.
    /// <c>using (context.Waiting("job 17 to finish")) { ... }</c>.
    /// </summary>
    /// <param name="text">
    /// What the task is waiting for, in one line for the operator, or <see langword="null"/> for none. It is kept as given.
    /// </param>
    /// <returns>The scope. Dispose it when the wait is over. Disposing it again does nothing.</returns>
    /// <remarks>
    /// <para>
    /// Waiting is a marker on a running task and never a status. Dependencies, group status, the run outcome and the scheduler
    /// ignore it, and a waiting task keeps the slots it holds in the cap and in its limits. Progress, warnings and the outcome
    /// work as usual while the task waits.
    /// </para>
    /// <para>
    /// The marker begins when the task's first open scope opens, with that scope's text and the time on the run's clock,
    /// and ends when its last open scope closes. Scopes that overlap therefore make one wait, with the text and time of the one
    /// that opened first. Each begin and end is published to reporters as a <see cref="WaitingChanged"/>, and while the task
    /// waits its <see cref="NodeSnapshot.WaitingSince"/> and <see cref="NodeSnapshot.WaitingText"/> are set. A task may wait
    /// several times in one run.
    /// </para>
    /// <para>
    /// A task that settles with a scope still open stops waiting immediately before it settles, and disposing that scope later
    /// does nothing. A scope opened once the task has settled does nothing at all.
    /// </para>
    /// </remarks>
    public IDisposable Waiting(string? text = null)
        => new WaitingScope(_run.OpenWaitingScope(_task, text) ? _run : null, _task);

    /// <summary>
    /// Waits for an external system: checks at once, then again one interval after each check ends, until the check
    /// returns <see langword="true"/>. The task is marked as waiting throughout.
    /// <c>await context.WaitUntil(TimeSpan.FromSeconds(30), async token => await jobs.IsFinishedAsync(17, token), "job 17 to finish");</c>
    /// </summary>
    /// <param name="interval">
    /// How long to wait after a check that returned <see langword="false"/> before the next check starts, on the run's clock.
    /// It is measured from the end of one check to the start of the next, so a slow check never makes checks pile up.
    /// </param>
    /// <param name="check">
    /// Asks the external system whether the work is done. It receives <see cref="CancellationToken"/> to pass to what it awaits.
    /// It may report progress, warnings and the outcome on this context, and may <c>throw context.Fail(...)</c>.
    /// </param>
    /// <param name="text">
    /// What the task is waiting for, in one line for the operator, or <see langword="null"/> for none. It is the text of the
    /// waiting scope the helper opens, as <see cref="Waiting"/> takes it.
    /// </param>
    /// <returns>A task that completes once a check has returned <see langword="true"/>.</returns>
    /// <remarks>
    /// <para>
    /// The helper opens a waiting scope with <paramref name="text"/> (see <see cref="Waiting"/>), calls the check, and while
    /// the check returns <see langword="false"/> delays for <paramref name="interval"/> on <see cref="TimeProvider"/> and calls
    /// it again. The scope closes however the helper ends: when the check returns <see langword="true"/>, when the check throws,
    /// and on a stop. A task waits for as long as it is inside the helper, time spent in the check included. A hand-written loop
    /// inside a <see cref="Waiting"/> scope is marked exactly the same way.
    /// </para>
    /// <para>
    /// A stop ends the helper at once: a delay in progress ends when <see cref="CancellationToken"/> is signalled, and the
    /// helper throws the <see cref="OperationCanceledException"/>, which makes the task <see cref="NodeStatus.Cancelled"/> if
    /// the body lets it escape. When the token is already signalled on entry the helper throws without opening a scope or
    /// calling the check. A check already running when the stop arrives is not interrupted: it sees the token and ends as it
    /// chooses. The helper does nothing to the external work, which carries on.
    /// </para>
    /// <para>
    /// An exception from the check is not caught, and ends the helper. An ordinary exception that escapes the body fails the
    /// task as an unexpected failure, and <c>throw context.Fail(...)</c> inside the check fails it as a reported failure.
    /// To ride out a transient error, catch it inside the check and return <see langword="false"/>: the wait goes on.
    /// </para>
    /// <para>
    /// There is no deadline, backoff or jitter, and tasks waiting at the same interval are not staggered. To give up after a
    /// while, compare the elapsed time on <see cref="TimeProvider"/> inside the check and fail the task.
    /// While the task waits it keeps the slots it holds in the cap and in its limits.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="interval"/> is zero or negative. It is thrown by the call itself, before any task is returned.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="check"/> is <see langword="null"/>. It is thrown by the call itself.</exception>
    /// <exception cref="OperationCanceledException">The run was stopped before the check returned <see langword="true"/>.</exception>
    public Task WaitUntil(TimeSpan interval, Func<CancellationToken, Task<bool>> check, string? text = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(check);

        return CheckUntilDone(interval, check, text, CancellationToken);
    }

    /// <summary>Reads the status, and nothing else, of a node this task depends on.</summary>
    /// <param name="dependency">The handle of the node, as the builder returned it. Handles stay valid for this after the graph is built.</param>
    /// <returns>
    /// The status of the node as it is at the call. A group's status is derived from the tasks beneath it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A task depends on every upstream declared on it with <c>Requires</c> or <c>After</c>, on every upstream declared on a group
    /// that holds it, and on every node, task or group, beneath an upstream that is a group, at any depth. A task that runs
    /// <em>after</em> a group can therefore ask which of the tasks beneath it failed.
    /// </para>
    /// <para>
    /// Since a task starts only once its dependencies are met, every node it can ask about has settled by the time its body runs:
    /// an upstream it requires has succeeded, and one it runs after has succeeded, failed or been blocked.
    /// The method answers at any time, including after this task has settled, and from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dependency"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// This task does not depend on <paramref name="dependency"/>: it is another node of the graph, this task itself,
    /// a group that holds it, or a handle of another graph.
    /// </exception>
    public NodeStatus StatusOf(Node dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);

        return _run.StatusOf(_task, dependency);
    }

    /// <summary>
    /// The task's checkpoint: what a run that continues the journal handed back when the task started, or what the task last saved
    /// in this run. Empty when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A checkpoint is a small piece of text the task keeps for a later run of itself, typically the id of work it started in an external
    /// system, so that a later run re-attaches to that work instead of starting it a second time. Dagline never reads it, and it is in no
    /// event and no snapshot, so no reporter ever shows it.
    /// </para>
    /// <para>
    /// The journal keeps a checkpoint through a failure, a cancellation, a task the run let go of and a killed process, and discards it
    /// when the task succeeds. A task that succeeded therefore starts with none if it runs again, and so does every task on a new journal.
    /// A checkpoint whose path is not in the graph of a later run is kept, and handed back again if the path returns.
    /// </para>
    /// <para>
    /// With <see cref="Journal.None"/> a checkpoint lives in memory for the run: it starts empty and reads back what the task saved.
    /// The property can be read at any time, from any thread, including after the task has settled.
    /// </para>
    /// </remarks>
    public string Checkpoint => _run.CheckpointOf(_task);

    /// <summary>
    /// Saves the task's checkpoint, which replaces any it had, and completes once the checkpoint is flushed to the disk.
    /// <c>await context.SaveCheckpointAsync(job.Id);</c>.
    /// </summary>
    /// <param name="text">
    /// The checkpoint: any text of at most 4,096 bytes as UTF-8. An empty string clears the checkpoint, as
    /// <see cref="ClearCheckpointAsync"/> does.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the save only if it is signalled before the save begins to write: once begun, the save completes. Do not pass
    /// <see cref="CancellationToken"/> to a save that records work the task has just started, since a stop would then cancel the save
    /// and a later run would start the work again.
    /// </param>
    /// <returns>A task that completes once the checkpoint is on disk, and at once for a run with no journal.</returns>
    /// <remarks>
    /// <para>
    /// The save writes one line to the journal and flushes it to the disk before it completes, so what the task does next can rely on the
    /// checkpoint surviving a killed process. The flush holds up the run for its length, roughly 1 to 5 ms on an SSD: a checkpoint is for
    /// milestones, not for progress. The save publishes no event and changes no snapshot. <see cref="Checkpoint"/> reads the new text
    /// from the moment the save completes.
    /// </para>
    /// <para>
    /// A save made during the grace period, after a stop, succeeds and is handed back in the next run. One made once the task has settled,
    /// or after the run has let go of it, throws: nothing would be kept, and the task must not believe otherwise.
    /// </para>
    /// <para>
    /// With <see cref="Journal.None"/> the checkpoint is kept in memory for the run, nothing is written, and the same rules apply.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>. It is thrown by the call itself.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="text"/> is over 4,096 bytes as UTF-8, measured in bytes and not characters. It is thrown by the call itself,
    /// and nothing is saved.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled before the save began. Nothing is saved.</exception>
    /// <exception cref="InvalidOperationException">The task has settled, or the run has let go of it. Nothing is saved.</exception>
    public Task SaveCheckpointAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > _checkpointBytes)
        {
            throw new ArgumentException(
                $"A checkpoint is at most {_checkpointBytes} bytes as UTF-8, and this one is {Encoding.UTF8.GetByteCount(text)}.",
                nameof(text));
        }

        return ChangeCheckpoint(text, cancellationToken);
    }

    /// <summary>
    /// Clears the task's checkpoint, and completes once the clearing is flushed to the disk. A task with no checkpoint writes nothing.
    /// </summary>
    /// <param name="cancellationToken">Cancels the clearing only if it is signalled before the clearing begins to write.</param>
    /// <returns>A task that completes once the clearing is on disk, and at once for a run with no journal or a task with no checkpoint.</returns>
    /// <remarks>
    /// Clear the checkpoint before reporting a failure when the external work has failed and a later run should start fresh work
    /// instead of re-attaching to it. It follows the rules of <see cref="SaveCheckpointAsync"/>: it succeeds during the grace period,
    /// and publishes no event.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled before the clearing began.</exception>
    /// <exception cref="InvalidOperationException">The task has settled, or the run has let go of it. Nothing is cleared.</exception>
    public Task ClearCheckpointAsync(CancellationToken cancellationToken = default) => ChangeCheckpoint("", cancellationToken);

    private Task ChangeCheckpoint(string text, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        try
        {
            _run.ChangeCheckpoint(_task, text);
            return Task.CompletedTask;
        }
        catch (InvalidOperationException notRunning)
        {
            return Task.FromException(notRunning);
        }
    }

    private async Task CheckUntilDone(
        TimeSpan interval,
        Func<CancellationToken, Task<bool>> check,
        string? text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using (Waiting(text))
        {
            while (!await check(cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(interval, TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ThrowIfAnyNull(string[] lines, string paramName)
    {
        ArgumentNullException.ThrowIfNull(lines, paramName);
        Arguments.ThrowIfAnyNull(lines, "A line is null.", paramName);
    }
}
