namespace Dagline;

/// <summary>
/// The journals in a directory as <see cref="Journals.ListAsync"/> found them: a summary of each journal it could read, and each one
/// it could not, apart, so that one bad file never stops an app from listing, continuing or deleting the others.
/// </summary>
/// <remarks>
/// Every file in the directory named <c>&lt;id&gt;.journal</c> with an id in the id format is in exactly one of the two lists, so no
/// journal is ever missing from a listing unseen. A listing is read at one moment and does not change after.
/// </remarks>
public sealed class JournalListing
{
    internal JournalListing(IReadOnlyList<JournalSummary> journals, IReadOnlyList<UnreadableJournal> unreadable)
    {
        Journals = journals;
        Unreadable = unreadable;
    }

    /// <summary>
    /// A summary of each journal that could be read, in order of creation, oldest first, and by id among journals created in the
    /// same second. Empty for a directory that does not exist.
    /// </summary>
    public IReadOnlyList<JournalSummary> Journals { get; }

    /// <summary>
    /// Each journal file that could not be read, with the reason, in ordinal order of its path. Empty when every journal could be
    /// read. An app shows these to an operator, who may fix or delete the file.
    /// </summary>
    public IReadOnlyList<UnreadableJournal> Unreadable { get; }
}
