namespace Dagline;

/// <summary>
/// How many tasks have each status, with the sums and sub-counts a reporter asks of them. Immutable.
/// </summary>
/// <remarks>
/// <para>
/// The run's counts are <see cref="Snapshot.Counts"/>, a node's are <see cref="NodeSnapshot.Counts"/>, and the counts of a group
/// that has just settled are <see cref="GroupSettled.Counts"/>. A group's counts are those of the tasks beneath it at any depth,
/// and a task's are those of the task alone: one for its status, with <see cref="Waiting"/> and <see cref="EarlierSuccesses"/>
/// one when they hold.
/// </para>
/// <para>
/// Only Dagline makes one. It is not a dictionary and is not enumerable: read the count of each status by its indexer.
/// </para>
/// </remarks>
public sealed class TaskCounts
{
    private static readonly NodeStatus[] _statuses = Enum.GetValues<NodeStatus>();

    private static readonly TaskCounts[] _ofOneTask =
        [.. _statuses.Select(status => OfOne(status, waiting: false, earlierSuccess: false))];

    private static readonly TaskCounts _ofOneWaitingTask = OfOne(NodeStatus.Running, waiting: true, earlierSuccess: false);
    private static readonly TaskCounts _ofOneEarlierSuccess = OfOne(NodeStatus.Succeeded, waiting: false, earlierSuccess: true);

    private readonly int[] _numbers;

    private TaskCounts(int[] numbers) => _numbers = numbers;

    /// <summary>Where a tally holds the earlier successes: after the count of each status.</summary>
    internal static int EarlierSuccessesAt => _statuses.Length;

    /// <summary>Where a tally holds the waiting: after the earlier successes.</summary>
    internal static int WaitingAt => _statuses.Length + 1;

    /// <summary>How many numbers a tally of counts takes: one for each status, then the earlier successes, then the waiting.</summary>
    internal static int TallyLength => _statuses.Length + 2;

    /// <summary>How many of the tasks have <paramref name="status"/>. Zero for a status no task has.</summary>
    /// <param name="status">A status.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not a defined status.</exception>
    public int this[NodeStatus status]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfLessThan((int)status, 0, nameof(status));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((int)status, _statuses.Length, nameof(status));
            return _numbers[(int)status];
        }
    }

    /// <summary>How many tasks are counted, whatever their status.</summary>
    public int Total
    {
        get
        {
            var total = 0;
            foreach (var status in _statuses)
            {
                total += _numbers[(int)status];
            }

            return total;
        }
    }

    /// <summary>
    /// How many of the tasks have settled: those whose status is settled, as <see cref="NodeStatusExtensions.IsSettled"/> says.
    /// They are the succeeded, failed, blocked and cancelled.
    /// </summary>
    public int Settled
    {
        get
        {
            var settled = 0;
            foreach (var status in _statuses)
            {
                if (status.IsSettled())
                {
                    settled += _numbers[(int)status];
                }
            }

            return settled;
        }
    }

    /// <summary>
    /// How many of the tasks have not succeeded: <see cref="Total"/> less the succeeded. It counts the tasks still to run as well as
    /// those that failed, were blocked or were cancelled, so it is the work a later run of the journal has left.
    /// </summary>
    public int Unsucceeded => Total - _numbers[(int)NodeStatus.Succeeded];

    /// <summary>
    /// How many of the tasks are earlier successes: kept from an earlier run of the journal and not run in this one.
    /// They are among the succeeded, counted apart so that a reporter can show how much this run did. Zero for a run with no journal.
    /// </summary>
    /// <remarks>It is fixed when the run starts and is the same in every snapshot of the run.</remarks>
    public int EarlierSuccesses => _numbers[EarlierSuccessesAt];

    /// <summary>
    /// How many of the running tasks are waiting, as <see cref="NodeSnapshot.IsWaiting"/> says of each: they have a
    /// <see cref="TaskContext.Waiting"/> scope open. They are among the running, and <see cref="Working"/> counts the rest of them.
    /// </summary>
    /// <remarks>A task that settles stops waiting as it settles, so a group that has settled has no waiting task beneath it.</remarks>
    public int Waiting => _numbers[WaitingAt];

    /// <summary>
    /// How many of the running tasks are working: running with no waiting scope open. With <see cref="Waiting"/> it makes up
    /// the running, and no task is counted in both.
    /// </summary>
    public int Working => _numbers[(int)NodeStatus.Running] - Waiting;

    /// <summary>The counts in <paramref name="tally"/>, laid out as <see cref="TallyLength"/> says. They are copied.</summary>
    internal static TaskCounts From(ReadOnlySpan<int> tally) => new(tally[..TallyLength].ToArray());

    /// <summary>The counts of one task taken alone. Every task with the same three answers shares one.</summary>
    internal static TaskCounts OfOneTask(NodeStatus status, bool waiting, bool earlierSuccess)
        => waiting ? _ofOneWaitingTask
            : earlierSuccess ? _ofOneEarlierSuccess
            : _ofOneTask[(int)status];

    private static TaskCounts OfOne(NodeStatus status, bool waiting, bool earlierSuccess)
    {
        var numbers = new int[TallyLength];
        numbers[(int)status] = 1;
        numbers[EarlierSuccessesAt] = earlierSuccess ? 1 : 0;
        numbers[WaitingAt] = waiting ? 1 : 0;
        return new TaskCounts(numbers);
    }
}
