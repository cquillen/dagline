namespace Dagline.TestSupport;

/// <summary>
/// The one write of a real journal that a test makes fail during a run, chosen by name:
/// <c>JournalWriteFailure.SettleOf("load").In(Journal.New(temporary.Path))</c>. Every other write reaches the real file.
/// </summary>
/// <remarks>
/// <para>
/// It is the public face of the one internal test hook on <see cref="Journal"/>, which wraps the stream of that journal's file once
/// it is opened, so that the first write holding the chosen entry throws an <see cref="IOException"/>. The hook applies to the one
/// journal it is put on, so tests running in parallel cannot fail each other's writes.
/// </para>
/// <para>
/// A choice names its entry as a <see cref="JournalLine"/>, the only code outside <c>JournalFile</c> that knows how an entry looks in
/// the journal's lines, and through which <see cref="JournalEntries"/> reads a journal back. So a change to the format touches
/// <see cref="JournalLine"/> and no test. A new choice is a new factory here that names one more kind of line.
/// </para>
/// </remarks>
public sealed class JournalWriteFailure
{
    private const string _defaultMessage = "The test made this journal write fail.";

    private readonly JournalLine _line;
    private readonly string _message;

    private JournalWriteFailure(JournalLine line, string message = _defaultMessage)
    {
        _line = line;
        _message = message;
    }

    /// <summary>Fails the write that holds the settle of the task at <paramref name="taskPath"/>, whatever it settled with.</summary>
    /// <param name="taskPath">The task's path as text, such as <c>shard-1/load</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="taskPath"/> is <see langword="null"/>, empty or only white space.</exception>
    public static JournalWriteFailure SettleOf(string taskPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskPath);

        return new JournalWriteFailure(JournalLine.SettleOf(taskPath));
    }

    /// <summary>
    /// Fails the write that holds a checkpoint save of the task at <paramref name="taskPath"/>, the first one it makes. A clearing
    /// is not a save, and is written.
    /// </summary>
    /// <param name="taskPath">The task's path as text, such as <c>shard-1/load</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="taskPath"/> is <see langword="null"/>, empty or only white space.</exception>
    public static JournalWriteFailure CheckpointSaveOf(string taskPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskPath);

        return new JournalWriteFailure(JournalLine.CheckpointSaveOf(taskPath));
    }

    /// <summary>
    /// Fails the write that holds the run's end: the last write of a run that ends with a run outcome. The run keeps its outcome,
    /// and only the journal shows the failure, by recording no end.
    /// </summary>
    public static JournalWriteFailure EndOfRun() => new(JournalLine.RunEnd);

    /// <summary>
    /// The same write, failing with an <see cref="IOException"/> whose message is <paramref name="message"/>, as a file system's own
    /// failure would word it, for a test of how a reporter shows the error the run ends with.
    /// </summary>
    /// <param name="message">The message, such as <c>disk full</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> is <see langword="null"/>, empty or only white space.</exception>
    public JournalWriteFailure WithMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return new JournalWriteFailure(_line, message);
    }

    /// <summary>The same journal choice, but with this write failing once the run opens the journal.</summary>
    /// <param name="journal">A new journal or one to continue, as <see cref="Journal.New"/> or <see cref="Journal.Continue"/> made it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is <see langword="null"/>.</exception>
    public Journal In(Journal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        return journal.WithFailingWrite(FailureOf);
    }

    private IOException? FailureOf(string write)
        => write.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => JournalLine.Of(line) == _line)
            ? new IOException(_message)
            : null;
}
