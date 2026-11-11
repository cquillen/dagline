namespace Dagline;

/// <summary>
/// A live source of snapshots of one run. A reporter receives it on <see cref="RunStarted"/>
/// and can read it at any time, for example on a timer of its own.
/// </summary>
public interface ISnapshotSource
{
    /// <summary>
    /// The run as it is now. This may be ahead of the events a reporter has been handed so far.
    /// Once the run has ended it is the final snapshot.
    /// </summary>
    /// <remarks>Safe to read from any thread. Reading it never waits for the run.</remarks>
    Snapshot Current { get; }
}
