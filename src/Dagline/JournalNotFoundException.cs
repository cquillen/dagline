namespace Dagline;

/// <summary>
/// Thrown when a call names a journal that is not in its directory: by <see cref="Runner.RunAsync"/> when
/// <see cref="Journal.Continue"/> names one, before a run exists, and by <see cref="Journals.DeleteAsync"/>.
/// </summary>
/// <remarks>
/// The directory may be missing, or may hold no journal with the id. From <see cref="Runner.RunAsync"/> the exception is thrown at
/// the call: no task has started, no event has been published and no reporter has been called.
/// <see cref="Exception.InnerException"/> is the file system's own exception.
/// </remarks>
public sealed class JournalNotFoundException : DaglineException
{
    internal JournalNotFoundException(JournalLocation location, Exception innerException)
        : base($"The directory '{location.Directory}' has no journal with the id '{location.Id}'.", innerException)
    {
        Id = location.Id;
        Directory = location.Directory;
    }

    /// <summary>The id that names no journal, as the app gave it.</summary>
    public string Id { get; }

    /// <summary>The directory that has no journal with <see cref="Id"/>, as the app gave it. It may not exist.</summary>
    public string Directory { get; }
}
