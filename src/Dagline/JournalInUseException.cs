namespace Dagline;

/// <summary>
/// Thrown when a run holds the journal that a call needs: by <see cref="Runner.RunAsync"/> on a journal another run is on,
/// before a run exists, and by <see cref="Journals.DeleteAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// A run holds its journal for its whole life through a lock file beside it, <c>&lt;id&gt;.lock</c>, open for exclusive use, and lets
/// go of it when it ends, however it ends. The holder may be in this process or in another, on this machine. The lock is enforced by the
/// operating system on Windows and is advisory elsewhere, which is enough because only Dagline needs to respect it.
/// </para>
/// <para>
/// From <see cref="Runner.RunAsync"/> it is thrown at the call, after the options have been checked and before the journal is opened:
/// no task has started, no event has been published and no reporter has been called. <see cref="Journals.ListAsync"/> shows such
/// a journal as <see cref="JournalEnding.InUse"/>.
/// </para>
/// <para><see cref="Exception.InnerException"/> is the file system's own exception.</para>
/// </remarks>
public sealed class JournalInUseException : DaglineException
{
    internal JournalInUseException(JournalLocation location, Exception innerException)
        : base($"The journal '{location.Id}' in '{location.Directory}' is in use: another run holds it.", innerException)
    {
        Id = location.Id;
        Directory = location.Directory;
    }

    /// <summary>The id of the journal a run holds.</summary>
    public string Id { get; }

    /// <summary>The directory of the journal a run holds, as the app gave it.</summary>
    public string Directory { get; }
}
