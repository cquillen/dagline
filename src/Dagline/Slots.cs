namespace Dagline;

/// <summary>
/// The slots of one run: how many tasks hold the cap, kept up to date as tasks start and settle.
/// It is not safe to use from several threads at once: the run that owns it changes it one step at a time.
/// </summary>
internal sealed class Slots(int cap)
{
    private int _running;

    /// <summary>
    /// Takes the slots a task holds from start to settle, if all of them are free. A task that does not fit takes none.
    /// </summary>
    /// <returns><see langword="true"/> when the task now holds its slots and may start.</returns>
    public bool TryTake()
    {
        if (_running == cap)
        {
            return false;
        }

        _running++;
        return true;
    }

    /// <summary>Gives back the slots a task took when it started.</summary>
    public void GiveBack() => _running--;
}
