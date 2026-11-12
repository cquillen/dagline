namespace Dagline;

/// <summary>
/// Thrown when a file that should be a journal cannot be read as one: by <see cref="Runner.RunAsync"/> before a run exists, and by
/// <see cref="Journals.ListAsync"/>, which names the journal in the message.
/// </summary>
/// <remarks>
/// <para>
/// The file's first line is not a journal header, the header gives a format version this Dagline cannot read, or a line other
/// than the last is not an entry. A journal written by a later Dagline has a version this one may not know: <see cref="Version"/>
/// carries it.
/// </para>
/// <para>
/// From <see cref="Runner.RunAsync"/> it is thrown at the call: no task has started, no event has been published and no reporter has
/// been called. The file is left as it is.
/// </para>
/// </remarks>
public sealed class JournalFormatException : DaglineException
{
    internal JournalFormatException(string path, string reason, int? version = null)
        : base($"The file '{path}' cannot be read as a journal: {reason}")
    {
        Version = version;
    }

    /// <summary>
    /// The format version the file's header gives, or <see langword="null"/> when the file has no header that could be read.
    /// </summary>
    public int? Version { get; }
}
