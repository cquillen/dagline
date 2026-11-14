namespace Dagline;

/// <summary>
/// A journal file that <see cref="Journals.ListAsync"/> found and could not read, in <see cref="JournalListing.Unreadable"/>.
/// </summary>
/// <remarks>
/// The file may be in a format version this Dagline does not know, as a journal written by a later Dagline may be; it may not be a
/// journal at all, or have been cut short or overwritten so that its first line is no longer a header; or the file or its lock file
/// may not be readable by the process. <see cref="Journal.Continue"/> on its id throws an exception of the same type, before a run exists.
/// </remarks>
public sealed class UnreadableJournal
{
    internal UnreadableJournal(string path, DaglineException exception)
    {
        Path = path;
        Exception = exception;
    }

    /// <summary>The full path of the journal file, <c>&lt;id&gt;.journal</c> in the directory listed.</summary>
    public string Path { get; }

    /// <summary>
    /// Why it could not be read: a <see cref="JournalFormatException"/> when the file cannot be read as a journal, with the format
    /// version when its header gives one, or a <see cref="JournalAccessException"/> when the file or its lock file cannot be opened or
    /// read. The message names the file.
    /// </summary>
    public DaglineException Exception { get; }
}
