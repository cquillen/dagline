using System.Collections.ObjectModel;

namespace Dagline;

/// <summary>How many tasks have each status, kept up to date as tasks change status.</summary>
internal sealed class StatusCounts
{
    private static readonly NodeStatus[] Statuses = Enum.GetValues<NodeStatus>();

    private readonly int[] _counts = new int[Statuses.Length];

    public StatusCounts(int pending)
    {
        _counts[(int)NodeStatus.Pending] = pending;
    }

    public int this[NodeStatus status] => _counts[(int)status];

    public void Move(NodeStatus from, NodeStatus to)
    {
        _counts[(int)from]--;
        _counts[(int)to]++;
    }

    /// <summary>The counts as they are now, with every status present, in lifecycle order.</summary>
    public ReadOnlyDictionary<NodeStatus, int> ToDictionary()
        => new(Statuses.ToDictionary(status => status, status => this[status]));
}
