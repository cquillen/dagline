using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Dagline;

/// <summary>
/// The slots of one run: how many are in use of the cap and of each limit the graph names,
/// kept up to date as tasks start and settle. The cap behaves as a limit that every task holds.
/// </summary>
/// <remarks>
/// It is not safe to use from several threads at once: <see cref="RunSteps"/> owns it and changes it one step at a time.
/// </remarks>
/// <param name="limitNames">Every limit the graph names, as <see cref="Graph.LimitNames"/> gives them.</param>
/// <param name="sizes">The size of the cap and of each of those limits.</param>
internal sealed class Slots(ImmutableArray<LimitName> limitNames, LimitSizes sizes)
{
    private readonly int[] _inUse = new int[sizes.Limits.Length];
    private int _running;

    /// <summary>How many numbers a tally of the slots in use takes: one for the cap, then one for each limit.</summary>
    public int TallyLength => 1 + _inUse.Length;

    private bool CapIsFull => _running == sizes.Cap;

    /// <summary>
    /// Writes the slots in use now to the start of <paramref name="tally"/>: the cap's, then each limit's,
    /// in the order the graph first names them.
    /// </summary>
    public void CopyTo(Span<int> tally)
    {
        tally[0] = _running;
        _inUse.CopyTo(tally[1..]);
    }

    /// <summary>The cap with its size and the slots in use that <see cref="CopyTo"/> wrote to <paramref name="tally"/>.</summary>
    public LimitUse CapIn(ReadOnlySpan<int> tally) => new(sizes.Cap, tally[0]);

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
    /// Takes one slot of the cap and of every limit a task holds, if each has one free.
    /// A task that does not fit takes none, so it never holds one slot while it waits for another.
    /// The cap is looked at first, since every task holds a slot of it.
    /// </summary>
    public SlotsAnswer Take(BuiltTask task)
    {
        if (CapIsFull)
        {
            return SlotsAnswer.CapFull;
        }

        if (task.HeldLimits.Any(limit => _inUse[limit] == sizes.Limits[limit]))
        {
            return SlotsAnswer.LimitFull;
        }

        _running++;
        foreach (var limit in task.HeldLimits)
        {
            _inUse[limit]++;
        }

        return SlotsAnswer.Took;
    }

    /// <summary>Gives back the slots a task took when it started.</summary>
    public void GiveBack(BuiltTask task)
    {
        _running--;
        foreach (var limit in task.HeldLimits)
        {
            _inUse[limit]--;
        }
    }
}
