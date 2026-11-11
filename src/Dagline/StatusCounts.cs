using System.Collections.ObjectModel;

namespace Dagline;

/// <summary>
/// How many tasks have each status, kept up to date as tasks change status.
/// One of these counts the tasks of a whole run, and one counts the tasks beneath each group.
/// </summary>
internal sealed class StatusCounts
{
    private static readonly NodeStatus[] _statuses = Enum.GetValues<NodeStatus>();

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

    private static readonly ReadOnlyDictionary<NodeStatus, int>[] _ofOneTask =
    [
        .. _statuses.Select(only => new ReadOnlyDictionary<NodeStatus, int>(
            _statuses.ToDictionary(status => status, status => status == only ? 1 : 0))),
    ];

    private readonly int[] _counts = new int[_statuses.Length];

    public StatusCounts(int pending)
    {
        _counts[(int)NodeStatus.Pending] = pending;
    }


    public int this[NodeStatus status] => _counts[(int)status];

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

    /// <summary>The counts of one task taken alone: one for its status and zero for every other.</summary>
    public static ReadOnlyDictionary<NodeStatus, int> OfOneTask(NodeStatus status) => _ofOneTask[(int)status];

    public void Move(NodeStatus from, NodeStatus to)
    {
        _counts[(int)from]--;
        _counts[(int)to]++;
    }

    /// <summary>How many numbers a tally of the counts takes: one for each status.</summary>
    public static int TallyLength => _statuses.Length;

    /// <summary>Writes the counts as they are now to the start of <paramref name="tally"/>, one number for each status.</summary>
    public void CopyTo(Span<int> tally) => _counts.CopyTo(tally);

    /// <summary>The counts as they are now, with every status present, in lifecycle order.</summary>
    public ReadOnlyDictionary<NodeStatus, int> ToDictionary() => ToDictionary(_counts);

    /// <summary>
    /// The counts that <see cref="CopyTo"/> wrote to the start of <paramref name="tally"/>, as <see cref="ToDictionary()"/> gives them.
    /// </summary>
    public static ReadOnlyDictionary<NodeStatus, int> ToDictionary(ReadOnlySpan<int> tally)
    {
        // A loop, not LINQ: a run takes one of these for every event, and LINQ's delegate and iterator cost more than the dictionary.
        var counts = new Dictionary<NodeStatus, int>(_statuses.Length);
        foreach (var status in _statuses)
        {
            counts.Add(status, tally[(int)status]);
        }

        return new(counts);
    }
}
