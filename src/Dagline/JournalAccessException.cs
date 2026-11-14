namespace Dagline;

/// <summary>
/// Thrown when a journal's file, its lock file or its directory cannot be opened, created, read, written or deleted: by
/// <see cref="Runner.RunAsync"/> before a run exists, and by <see cref="Journals.ListAsync"/> and <see cref="Journals.DeleteAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The cause may be a permission the process lacks, a path that names something other than a directory, a full disk, or a failure
/// of the device. A new journal whose first line could not be written and flushed is this too.
/// </para>
/// <para>
/// The message names the file or the directory and says what could not be done with it: a directory that cannot be created or
/// listed, a lock file that cannot be opened or created, or a journal file that cannot be opened, created, read, deleted or given
/// its first line. It ends with the message of the file system's own exception.
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
    private JournalAccessException(string? id, string directory, string failure, Exception innerException)
        : base($"{failure}: {innerException.Message}", innerException)
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

    internal static JournalAccessException DirectoryNotCreated(string directory, Exception innerException)
        => OnDirectory(directory, "created", innerException);

    internal static JournalAccessException DirectoryNotListed(string directory, Exception innerException)
        => OnDirectory(directory, "listed", innerException);

    internal static JournalAccessException LockNotOpened(JournalLocation location, Exception innerException)
        => OnLock(location, "opened", innerException);

    internal static JournalAccessException LockNotOpenedOrCreated(JournalLocation location, Exception innerException)
        => OnLock(location, "opened or created", innerException);

    internal static JournalAccessException FileNotOpened(JournalLocation location, Exception innerException)
        => OnFile(location, "opened", innerException);

    internal static JournalAccessException FileNotCreated(JournalLocation location, Exception innerException)
        => OnFile(location, "created", innerException);

    internal static JournalAccessException FileNotRead(JournalLocation location, Exception innerException)
        => OnFile(location, "read", innerException);

    internal static JournalAccessException FileNotDeleted(JournalLocation location, Exception innerException)
        => OnFile(location, "deleted", innerException);

    internal static JournalAccessException HeaderNotWritten(JournalLocation location, Exception innerException)
        => new(
            location.Id,
            location.Directory,
            $"The first line of the new journal file '{location.FilePath}' cannot be written",
            innerException);

    private static JournalAccessException OnDirectory(string directory, string action, Exception innerException)
        => new(id: null, directory, $"The journal directory '{directory}' cannot be {action}", innerException);

    private static JournalAccessException OnLock(JournalLocation location, string action, Exception innerException)
        => new(
            location.Id,
            location.Directory,
            $"The lock file '{location.LockPath}' of the journal '{location.Id}' cannot be {action}",
            innerException);

    private static JournalAccessException OnFile(JournalLocation location, string action, Exception innerException)
        => new(location.Id, location.Directory, $"The journal file '{location.FilePath}' cannot be {action}", innerException);
}
