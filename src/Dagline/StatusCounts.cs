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

    /// <summary>The counts as they are now, with every status present, in lifecycle order.</summary>
    public ReadOnlyDictionary<NodeStatus, int> ToDictionary()
        => new(_statuses.ToDictionary(status => status, status => this[status]));
}
