namespace Dagline;

/// <summary>
/// How full a limit, or the max running tasks, is at one instant.
/// </summary>
/// <remarks>
/// A task takes a slot when it starts and gives it back when it settles, so <see cref="InUse"/> counts running tasks
/// and never ready ones. A limit is full when the two numbers are equal: a ready task that holds it is then passed over
/// until a task gives a slot back.
/// </remarks>
public sealed record LimitUse
{
    internal LimitUse(int size, int inUse)
    {
        Size = size;
        InUse = inUse;
    }

    /// <summary>How many slots there are: the most tasks holding the limit that may be running at once. At least 1.</summary>
    public int Size { get; }

    /// <summary>
    /// How many of the slots are held, one by each running task that holds the limit. From zero to <see cref="Size"/>.
    /// </summary>
    public int InUse { get; }
}
