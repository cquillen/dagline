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
/// been called. <see cref="Journals.ListAsync"/> throws it only when the directory itself cannot be read: a journal whose file or
/// lock file cannot be read is listed in <see cref="JournalListing.Unreadable"/> with this exception instead.
/// </para>
/// <para><see cref="Exception.InnerException"/> is the file system's own exception.</para>
/// </remarks>
public sealed class JournalAccessException : DaglineException
{
    /// <summary>A failure on the file or the lock file at <paramref name="path"/> of the journal at <paramref name="location"/>.</summary>
    internal JournalAccessException(JournalLocation location, string path, Exception innerException)
        : this(path, location.Id, location.Directory, innerException)
    {
    }

    /// <summary>A failure on <paramref name="directory"/> itself, which concerns no one journal.</summary>
    internal JournalAccessException(string directory, Exception innerException)
        : this(directory, id: null, directory, innerException)
    {
    }

    private JournalAccessException(string path, string? id, string directory, Exception innerException)
        : base($"The journal file '{path}' cannot be opened: {innerException.Message}", innerException)
    {
        Id = id;
        Directory = directory;
    }

    /// <summary>
    /// The id of the journal whose file or lock file could not be used, or <see langword="null"/> when the failure was on the
    /// directory itself: it could not be created or listed, so no one journal is concerned.
    /// </summary>
    public string? Id { get; }

    /// <summary>
    /// The journal's directory, or the directory that could not be created or listed, as the app gave it to <see cref="Journal.New"/>,
    /// <see cref="Journal.Continue"/>, <see cref="Journals.ListAsync"/> or <see cref="Journals.DeleteAsync"/>.
    /// </summary>
    public string Directory { get; }
}
