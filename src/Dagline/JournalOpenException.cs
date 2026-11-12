namespace Dagline;

/// <summary>
/// Thrown when a journal's file, its lock file or its directory cannot be opened, created or read: by <see cref="Runner.RunAsync"/>
/// before a run exists, and by <see cref="Journals.ListAsync"/> and <see cref="Journals.DeleteAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The cause may be a permission the process lacks, a path that names something other than a directory, a full disk, or a failure
/// of the device. A new journal whose first line could not be written and flushed is this too.
/// </para>
/// <para>
/// From <see cref="Runner.RunAsync"/> it is thrown at the call: no task has started, no event has been published and no reporter has
/// been called. <see cref="Exception.InnerException"/> is the file system's own exception.
/// </para>
/// </remarks>
public sealed class JournalOpenException : DaglineException
{
    internal JournalOpenException(string path, Exception innerException)
        : base($"The journal file '{path}' cannot be opened: {innerException.Message}", innerException)
    {
    }
}
