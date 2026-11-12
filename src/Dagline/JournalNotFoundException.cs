namespace Dagline;

/// <summary>
/// Thrown by <see cref="Runner.RunAsync"/> when <see cref="Journal.Continue"/> names a journal that is not in its directory,
/// before a run exists.
/// </summary>
/// <remarks>
/// The directory may be missing, or may hold no journal with the id. The exception is thrown at the call: no task has started,
/// no event has been published and no reporter has been called. <see cref="Exception.InnerException"/> is the file system's own
/// exception.
/// </remarks>
public sealed class JournalNotFoundException : DaglineException
{
    internal JournalNotFoundException(JournalLocation location, Exception innerException)
        : base($"The directory '{location.Directory}' has no journal with the id '{location.Id}'.", innerException)
    {
    }
}
