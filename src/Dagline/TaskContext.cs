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
/// <see cref="StatusOf"/> still answers. A body sees this only when it leaves work running after it has ended.
/// </para>
/// <para>
/// Dagline keeps the first 100 warnings and the first 100 detail lines of a task, counted separately, and only counts the rest,
/// in <see cref="NodeSnapshot.WarningsOmitted"/> and <see cref="NodeSnapshot.DetailOmitted"/>. A task that finds three thousand
/// problems therefore does not flood the run: report the count in the message and the first few in the detail.
/// </para>
/// </remarks>
public sealed class TaskContext
{
    private readonly Run _run;
    private readonly GraphTask _task;

    internal TaskContext(Run run, GraphTask task, TimeProvider timeProvider)
    {
        _run = run;
        _task = task;
        TimeProvider = timeProvider;
    }

    /// <summary>The path of the task this body is running as.</summary>
    public NodePath Path => _task.Path;

    /// <summary>
    /// The clock of the run, as given in <see cref="RunOptions.TimeProvider"/>.
    /// Take any delay or timestamp of the task's own from it, so that a test of the task controls time.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Sets the status text of the task's progress: a line that says what the task is doing now.</summary>
    /// <param name="text">The new status text, which replaces the last one. <see langword="null"/> clears it.</param>
    /// <remarks>
    /// The text shows as <see cref="NodeSnapshot.StatusText"/>. It is independent of the count: setting one leaves the other as it was.
    /// Progress is never an event, so calling this often costs a reporter nothing. The text is kept as given.
    /// </remarks>
    public void Status(string? text) => _run.Report(_task, task => task.WithStatusText(text));

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

        _run.Report(_task, task => task.WithCount(current, total));
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

        _run.Report(_task, task => task.WithWarning(warning));
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

        _run.Report(_task, task => task.WithMessage(message));
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

        _run.Report(_task, task => task.WithDetail(lines));
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

        _run.ReportFailure(_task, task => task.WithMessage(message).WithDetail(detail));

        return new ReportedFailureException(TaskReportRules.OneLine(message), [.. detail.SelectMany(TaskReportRules.Lines)]);
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

    private static void ThrowIfAnyNull(string[] lines, string paramName)
    {
        ArgumentNullException.ThrowIfNull(lines, paramName);

        if (lines.Contains(null))
        {
            throw new ArgumentException("A line is null.", paramName);
        }
    }
}
