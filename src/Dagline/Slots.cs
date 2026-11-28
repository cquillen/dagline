using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Dagline;

/// <summary>
/// The slots of one run: how many are in use of the max running tasks and of each limit the graph names,
/// kept up to date as tasks start and settle. The max running tasks behaves as a limit that every task holds.
/// </summary>
/// <remarks>
/// It is not safe to use from several threads at once: <see cref="RunSteps"/> owns it and changes it one step at a time.
/// </remarks>
/// <param name="limitNames">Every limit the graph names, as <see cref="Graph.LimitNames"/> gives them.</param>
/// <param name="sizes">The max running tasks and the size of each of those limits.</param>
internal sealed class Slots(ImmutableArray<LimitName> limitNames, LimitSizes sizes)
{
    private readonly int[] _inUse = new int[sizes.Limits.Length];
    private int _running;

    /// <summary>
    /// A number that changes each time a slot is taken or given back, so that a tally of the slots in use can be kept while it
    /// stays the same.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// How many times <see cref="Take"/> has been called, whatever it answered: the work of starting tasks, which a test reads to
    /// check that a run's tries grow with its number of tasks and not with their square.
    /// </summary>
    public int Tries { get; private set; }

    /// <summary>How many numbers a tally of the slots in use takes: one for the max running tasks, then one for each limit.</summary>
    public int TallyLength => 1 + _inUse.Length;

    private bool IsAtMaxRunningTasks => LimitUse.AreAllHeld(sizes.MaxRunningTasks, _running);

    /// <summary>
    /// Writes the slots in use now to the start of <paramref name="tally"/>: the max running tasks', then each limit's,
    /// in the order the graph first names them.
    /// </summary>
    public void CopyTo(Span<int> tally)
    {
        tally[0] = _running;
        _inUse.CopyTo(tally[1..]);
    }

    /// <summary>The max running tasks with its size and the slots in use that <see cref="CopyTo"/> wrote to <paramref name="tally"/>.</summary>
    public LimitUse RunningTasksIn(ReadOnlySpan<int> tally) => new(sizes.MaxRunningTasks, tally[0]);

    /// <summary>
    /// Each limit the graph names with its size and the slots in use that <see cref="CopyTo"/> wrote to <paramref name="tally"/>,
    /// in the order the graph first names them.
    /// </summary>
    public ReadOnlyDictionary<string, LimitUse> LimitsIn(ReadOnlySpan<int> tally)
    {
        // A loop, not LINQ: a run takes one of these for every event, and LINQ's delegate and iterator cost more than the dictionary.
        var limits = new Dictionary<string, LimitUse>(limitNames.Length, LimitName.TextComparer);
        for (var limit = 0; limit < limitNames.Length; limit++)
        {
            limits.Add(limitNames[limit].Text, new LimitUse(sizes.Limits[limit], tally[1 + limit]));
        }

        return new(limits);
    }

    /// <summary>
    /// Takes one slot of the max running tasks and of every limit a task holds, if each has one free.
    /// A task that does not fit takes none, so it never holds one slot while it waits for another.
    /// The max running tasks is looked at first, since every task holds a slot of it.
    /// </summary>
    public SlotsAnswer Take(BuiltTask task)
    {
        Tries++;
        if (IsAtMaxRunningTasks)
        {
            return SlotsAnswer.MaxRunningTasksReached;
        }

        if (FullLimitAmong(task.HeldLimits.AsSpan()) is not null)
        {
            return SlotsAnswer.LimitFull;
        }

        _running++;
        foreach (var limit in task.HeldLimits)
        {
            _inUse[limit]++;
        }

        Version++;

        return SlotsAnswer.Took;
    }

    /// <summary>
    /// The first of <paramref name="limits"/> that is full, each given as its position among <see cref="Graph.LimitNames"/>,
    /// or <see langword="null"/> when every one has room.
    /// </summary>
    // A loop, not LINQ: the ready tasks are tried after every settle, and a lambda over the slots would be allocated on each try.
    public int? FullLimitAmong(ReadOnlySpan<int> limits)
    {
        foreach (var limit in limits)
        {
            if (LimitUse.AreAllHeld(sizes.Limits[limit], _inUse[limit]))
            {
                return limit;
            }
        }

        return null;
    }

    /// <summary>The first limit the task holds that is full, which <see cref="Take"/> answered <see cref="SlotsAnswer.LimitFull"/> for.</summary>
    /// <exception cref="InvalidOperationException">None of the limits the task holds is full.</exception>
    public int FullLimitOf(BuiltTask task)
        => FullLimitAmong(task.HeldLimits.AsSpan())
            ?? throw new InvalidOperationException($"No limit the task '{task.Path}' holds is full.");

    /// <summary>Gives back the slots a task took when it started.</summary>
    public void GiveBack(BuiltTask task)
    {
        _running--;
        foreach (var limit in task.HeldLimits)
        {
            _inUse[limit]--;
        }

        Version++;
    }
}
