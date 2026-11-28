namespace Dagline;

/// <summary>
/// A run on the new engine as its tasks' contexts see it. What a task reports reaches the run either as an input posted to the
/// inbox, applied in the order the task made its calls, or as a sample the task overwrites in a place of its own (ADR 0015).
/// </summary>
/// <remarks>
/// A post returns at once, so what a task reports as an input shows in the run's current snapshot once the loop has applied it,
/// not when the call returns. A member not yet built throws <see cref="NotSupportedException"/>, so that a test run on the new
/// engine fails where it calls it, rather than passing over a report the run never applied.
/// </remarks>
/// <param name="inbox">The run's inbox, to which the inputs are posted.</param>
/// <param name="samples">The tasks' samples, which the reports that are samples overwrite.</param>
/// <param name="graph">The graph the run runs.</param>
/// <param name="source">The run's current snapshot, from which a dependency's status is read.</param>
internal sealed class LoopTaskRun(Inbox inbox, TaskSamples samples, Graph graph, CurrentSnapshot source) : ITaskRun
{
    // Each throw below is a departure from Liskov substitution for the length of the rebuild: the new engine is reachable only
    // from tests, and each member is built by a later part of it (checkpoints).
    /// <summary>Overwrites the status text on the task's sample: progress is never posted.</summary>
    public void SetStatusText(BuiltTask task, string? text) => samples[task].SetStatusText(text);

    /// <summary>Overwrites the count on the task's sample: progress is never posted.</summary>
    public void SetCount(BuiltTask task, long current, long? total) => samples[task].SetCount(current, total);

    /// <summary>Posts a warning that is kept; one beyond the kept is only counted on the task's sample.</summary>
    public void Warn(BuiltTask task, string warning)
    {
        if (samples[task].KeepWarning())
        {
            inbox.Post(Input.WarningKept(task, warning));
        }
    }

    public void SetMessage(BuiltTask task, string message) => inbox.Post(Input.MessageSet(task, message));

    /// <summary>Posts the detail lines that are kept; those beyond the kept are only counted on the task's sample.</summary>
    public void AddDetail(BuiltTask task, IEnumerable<string> texts)
    {
        var kept = KeptLines(task, texts);
        if (kept.Length > 0)
        {
            inbox.Post(Input.DetailKept(task, kept));
        }
    }

    /// <summary>
    /// Posts a reported failure, with the detail lines of it that are kept: the task's detail lines and its failure's share one
    /// limit, counted on its sample.
    /// </summary>
    public void Fail(BuiltTask task, string message, IEnumerable<string> detail)
        => inbox.Post(Input.FailureReported(task, message, KeptLines(task, detail)));

    /// <summary>
    /// Posts the opening of a waiting scope, always: only the core knows whether the task is still running, and it ignores the
    /// scope, and its close, for a task that is not.
    /// </summary>
    /// <returns><see langword="true"/>, so that closing the scope is posted too.</returns>
    public bool OpenWaitingScope(BuiltTask task, string? text)
    {
        inbox.Post(Input.WaitOpened(task, text));
        return true;
    }

    public void CloseWaitingScope(BuiltTask task) => inbox.Post(Input.WaitClosed(task));

    public string CheckpointOf(BuiltTask task) => throw NotYet(nameof(TaskContext.Checkpoint));

    public Task SaveCheckpointAsync(BuiltTask task, string text) => throw NotYet(nameof(TaskContext.SaveCheckpointAsync));

    /// <summary>
    /// The status of a node the task depends on, as of the last snapshot the loop published: the status of a dependency changes only
    /// with an event, so this is the status a reporter holding that event reads. It answers whether or not the task has settled.
    /// </summary>
    /// <exception cref="ArgumentException">The task does not depend on the node.</exception>
    public NodeStatus StatusOf(BuiltTask task, Node dependency)
        => source.Published.Nodes[graph.DependencyPositionOf(task, dependency)].Status;

    /// <summary>The lines of <paramref name="texts"/>, one for each line break, as far as the task's sample keeps them.</summary>
    private string[] KeptLines(BuiltTask task, IEnumerable<string> texts)
    {
        string[] lines = [.. texts.SelectMany(LineBreaks.Lines)];
        var kept = samples[task].KeepDetail(lines.Length);
        return kept == lines.Length ? lines : lines[..kept];
    }

    private static NotSupportedException NotYet(string member) => new($"The new engine does not yet take {member} from a task.");
}
