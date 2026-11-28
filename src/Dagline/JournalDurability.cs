namespace Dagline;

/// <summary>
/// What a run's journal entries survive: whether each is flushed to the disk or only handed to the operating system. It is chosen
/// for each run, on <see cref="Journal.New"/> or <see cref="Journal.Continue"/>, and a run that continues a journal chooses afresh,
/// as it does a limit's size.
/// </summary>
/// <remarks>
/// <para>
/// The choice changes only whether a write is flushed. Every entry reaches the operating system at the same moments under either:
/// a task's settle before any of its dependents starts and before its event is published, the run's start and its set-asides before
/// any task starts, a checkpoint save before the save completes, and the run's end before the run lets go of the journal. Entries
/// reach the file in the order they were made, and the file is the same under either, so a journal written with one choice is read,
/// listed, continued and cut short at a line exactly as one written with the other.
/// </para>
/// <para>
/// A write handed to the operating system is in the file for any process that reads it from then on, and the operating system
/// writes it to the disk when it chooses. So it survives the process being killed, but not a crash of the operating system or a
/// power cut, which can lose any entry written since the operating system last wrote to the disk, a recorded success or a saved
/// checkpoint included. A success lost that way runs again in the next run, with whatever checkpoint survived.
/// </para>
/// </remarks>
public enum JournalDurability
{
    /// <summary>
    /// The default: each settle, the run's start with its set-asides, each checkpoint save and the run's end are flushed to the
    /// disk, and so is a new journal's first line and its place in its directory. Entries written together share one flush, which
    /// costs roughly 1 to 5 ms on an SSD. No recorded success and no saved checkpoint is lost to a killed process, a crash of the
    /// operating system or a power cut, as far as the storage device honours a flush. The entry that says a task started is never
    /// flushed by itself, under either choice.
    /// </summary>
    FlushedToDisk,

    /// <summary>
    /// Each entry is handed to the operating system at the same moments as with <see cref="FlushedToDisk"/>, and nothing is ever
    /// flushed to the disk, not even a new journal's first line. No recorded success and no saved checkpoint is lost to a killed
    /// process, though any may be lost to a crash of the operating system or a power cut. For a run whose speed matters more than
    /// a power cut's safety, such as a test's.
    /// </summary>
    HandedToOperatingSystem,
}
