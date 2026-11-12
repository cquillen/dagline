namespace Dagline;

/// <summary>
/// How many tasks have each status, how many of them are earlier successes and how many are waiting, kept up to date as tasks
/// change. One of these counts the tasks of a whole run, and one counts the tasks beneath each group.
/// </summary>
/// <remarks>It is the mutable tally of the step core. A snapshot shows it as <see cref="TaskCounts"/>, which copies it.</remarks>
internal sealed class StatusCounts
{
    // A group that has not settled shows the furthest any of its tasks has got, and a settled one the worst that happened.
    private static readonly NodeStatus[] _groupStatusPrecedence =
    [
        NodeStatus.Running,
        NodeStatus.Ready,
        NodeStatus.Pending,
        NodeStatus.Failed,
        NodeStatus.Cancelled,
        NodeStatus.Blocked,
    ];

    // Laid out as TaskCounts reads a tally: the count of each status, then the earlier successes, then the waiting.
    private readonly int[] _tally = new int[TaskCounts.TallyLength];

    // The last copy handed out, until the counts next change.
    private TaskCounts? _shown;

    /// <param name="pending">How many tasks are pending at the start.</param>
    /// <param name="earlierSuccesses">How many tasks are earlier successes, and so succeeded from the start.</param>
    public StatusCounts(int pending, int earlierSuccesses)
    {
        _tally[(int)NodeStatus.Pending] = pending;
        _tally[(int)NodeStatus.Succeeded] = earlierSuccesses;
        _tally[TaskCounts.EarlierSuccessesAt] = earlierSuccesses;
    }

    public int this[NodeStatus status] => _tally[(int)status];

    /// <summary>
    /// The status of a group whose tasks, at any depth, these are.
    /// While any task has not settled: running if any is running, else ready if any is ready, else pending.
    /// Once all have settled: failed if any failed, else cancelled if any was cancelled, else blocked if any is blocked, else succeeded.
    /// A group with no tasks is therefore succeeded.
    /// </summary>
    public NodeStatus GroupStatus
    {
        get
        {
            foreach (var status in _groupStatusPrecedence)
            {
                if (this[status] > 0)
                {
                    return status;
                }
            }

            return NodeStatus.Succeeded;
        }
    }

    public void Move(NodeStatus from, NodeStatus to)
    {
        _tally[(int)from]--;
        _tally[(int)to]++;
        _shown = null;
    }

    /// <summary>Counts a task's wait as it begins or ends: one more waiting task, or one fewer.</summary>
    public void Count(WaitingChanged waited)
    {
        _tally[TaskCounts.WaitingAt] += waited.Waiting ? 1 : -1;
        _shown = null;
    }

    /// <summary>Writes the counts as they are now to the start of <paramref name="tally"/>, as <see cref="TaskCounts"/> reads them.</summary>
    public void CopyTo(Span<int> tally) => _tally.CopyTo(tally);

    /// <summary>
    /// The counts as they are now, copied. Until they next change, every call gives the same copy: a group whose counts stay the
    /// same while the tasks beneath it are given warnings shares one between its snapshots.
    /// </summary>
    public TaskCounts ToTaskCounts() => _shown ??= TaskCounts.From(_tally);
}
