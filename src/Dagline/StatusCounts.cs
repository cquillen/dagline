namespace Dagline;

/// <summary>
/// How many tasks have each status, how many of them are earlier successes and how many are waiting, kept up to date as tasks
/// change. One of these counts the tasks of a whole run, and one counts the tasks beneath each group.
/// </summary>
/// <remarks>
/// <see cref="RollUp"/> owns every one, and is the only code that changes them. A snapshot shows it as <see cref="TaskCounts"/>,
/// which copies it.
/// </remarks>
internal sealed class StatusCounts
{
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

    /// <summary>
    /// The counts as they are now, copied. Until they next change, every call gives the same copy: a group whose counts stay the
    /// same while the tasks beneath it are given warnings shares one between its snapshots.
    /// </summary>
    public TaskCounts ToTaskCounts() => _shown ??= TaskCounts.From(_tally);
}
