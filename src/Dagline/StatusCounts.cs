namespace Dagline;

/// <summary>How many tasks have each status, kept up to date as tasks change status.</summary>
internal sealed class StatusCounts
{
    private readonly int[] _counts = new int[Enum.GetValues<NodeStatus>().Length];

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
}
