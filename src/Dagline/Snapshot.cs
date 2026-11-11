namespace Dagline;

/// <summary>
/// Everything Dagline knows about a run at one instant. A snapshot is immutable and consistent:
/// it never changes after it is made, and everything in it is as of the same instant.
/// </summary>
public sealed class Snapshot
{
    internal Snapshot()
    {
    }
}
