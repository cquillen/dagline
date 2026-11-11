namespace Dagline;

/// <summary>
/// The slots of one run: how many are in use of the cap and of each limit the graph names,
/// kept up to date as tasks start and settle. The cap behaves as a limit that every task holds.
/// </summary>
/// <remarks>
/// It is not safe to use from several threads at once: the run that owns it changes it one step at a time.
/// </remarks>
internal sealed class Slots(LimitSizes sizes)
{
    private readonly int[] _inUse = new int[sizes.Limits.Length];
    private int _running;

    /// <summary>
    /// Takes one slot of the cap and of every limit a task holds, if each has one free.
    /// A task that does not fit takes none, so it never holds one slot while it waits for another.
    /// </summary>
    /// <returns><see langword="true"/> when the task now holds its slots and may start.</returns>
    public bool TryTake(GraphTask task)
    {
        if (_running == sizes.Cap || task.HeldLimits.Any(limit => _inUse[limit] == sizes.Limits[limit]))
        {
            return false;
        }

        _running++;
        foreach (var limit in task.HeldLimits)
        {
            _inUse[limit]++;
        }

        return true;
    }

    /// <summary>Gives back the slots a task took when it started.</summary>
    public void GiveBack(GraphTask task)
    {
        _running--;
        foreach (var limit in task.HeldLimits)
        {
            _inUse[limit]--;
        }
    }
}
