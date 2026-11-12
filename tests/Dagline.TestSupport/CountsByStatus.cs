namespace Dagline.TestSupport;

/// <summary>Reads counts as a dictionary of every status, in lifecycle order, so a test can compare them with one written out.</summary>
public static class CountsByStatus
{
    /// <summary>The count of each status in <paramref name="counts"/>, with every status present.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="counts"/> is <see langword="null"/>.</exception>
    public static Dictionary<NodeStatus, int> ByStatus(this TaskCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return Enum.GetValues<NodeStatus>().ToDictionary(status => status, status => counts[status]);
    }
}
