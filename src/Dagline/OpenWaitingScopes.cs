namespace Dagline;

/// <summary>
/// Counts the waiting scopes each task of a run has open, and so says when a task's waiting marker begins and ends:
/// it begins with the first open scope and ends with the last. It is not thread-safe: the run calls it under its lock.
/// </summary>
internal sealed class OpenWaitingScopes
{
    private readonly Dictionary<int, int> _open = [];

    /// <summary>Opens one more scope of the task at <paramref name="position"/>.</summary>
    /// <returns><see langword="true"/> when it is the task's only open scope: its marker begins.</returns>
    public bool Open(int position)
    {
        var open = _open.GetValueOrDefault(position) + 1;
        _open[position] = open;
        return open == 1;
    }

    /// <summary>Closes one scope of the task at <paramref name="position"/>, if it has any open.</summary>
    /// <returns><see langword="true"/> when that was the task's last open scope: its marker ends.</returns>
    public bool Close(int position)
    {
        if (!_open.TryGetValue(position, out var open))
        {
            return false;
        }

        if (open > 1)
        {
            _open[position] = open - 1;
            return false;
        }

        return _open.Remove(position);
    }

    /// <summary>Closes every scope of the task at <paramref name="position"/>, as when it settles.</summary>
    /// <returns><see langword="true"/> when the task had a scope open: its marker ends.</returns>
    public bool CloseAll(int position) => _open.Remove(position);
}
