using System.Text.Json;

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
/// This is the only code outside <c>JournalFile</c> that knows how an entry looks in the journal's lines, so a change to the format
/// touches the choices here and no test. A new choice is a new factory that recognises one more kind of line.
/// </para>
/// </remarks>
public sealed class JournalWriteFailure
{
    private readonly Func<string, bool> _picksLine;

    private JournalWriteFailure(Func<string, bool> picksLine) => _picksLine = picksLine;

    /// <summary>Fails the write that holds the settle of the task at <paramref name="taskPath"/>, whatever it settled with.</summary>
    /// <param name="taskPath">The task's path as text, such as <c>shard-1/load</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="taskPath"/> is <see langword="null"/>, empty or only white space.</exception>
    public static JournalWriteFailure SettleOf(string taskPath) => EntryOf("taskSettled", taskPath);

    /// <summary>The same journal choice, but with this write failing once the run opens the journal.</summary>
    /// <param name="journal">A new journal or one to continue, as <see cref="Journal.New"/> or <see cref="Journal.Continue"/> made it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is <see langword="null"/>.</exception>
    public Journal In(Journal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        return journal.WithFailingWrite(write => write.Split('\n').Any(_picksLine));
    }

    // Every line is one JSON object that opens with the kind of its entry, then the path for an entry about one task.
    private static JournalWriteFailure EntryOf(string kind, string taskPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskPath);

        var opening = $"{{\"entry\":\"{kind}\",\"path\":{JsonSerializer.Serialize(taskPath)},";
        return new JournalWriteFailure(line => line.StartsWith(opening, StringComparison.Ordinal));
    }
}
