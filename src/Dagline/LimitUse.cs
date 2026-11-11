namespace Dagline;

/// <summary>
/// How full a limit, or the cap, is at one instant.
/// </summary>
/// <param name="Size">How many slots there are: the most tasks holding the limit that may be running at once. At least 1.</param>
/// <param name="InUse">
/// How many of the slots are held, one by each running task that holds the limit. From zero to <paramref name="Size"/>.
/// </param>
/// <remarks>
/// A task takes a slot when it starts and gives it back when it settles, so <paramref name="InUse"/> counts running tasks
/// and never ready ones. A limit is full when the two numbers are equal: a ready task that holds it is then passed over
/// until a task gives a slot back.
/// </remarks>
public sealed record LimitUse(int Size, int InUse);
