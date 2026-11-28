namespace Dagline.TestSupport;

/// <summary>
/// Counts the flushes to the disk that a real journal makes during runs:
/// <c>var flushes = new JournalFlushes(); var journal = flushes.In(Journal.New(temporary.Path));</c>, then <c>flushes.Count</c>.
/// </summary>
/// <remarks>
/// It is the other public face of the internal test hook on <see cref="Journal"/> that <see cref="JournalWriteFailure"/> uses, so the
/// two compose: <c>flushes.In(JournalWriteFailure.SettleOf("load").In(Journal.New(temporary.Path)))</c>. Each flush is counted where the
/// journal file makes it, the header's included. One counter can be put on several journal choices, such as a new journal and the
/// run that continues it, and counts them all.
/// </remarks>
public sealed class JournalFlushes
{
    private int _count;

    /// <summary>How many flushes to the disk the journals this was put on have made so far.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The same journal choice, with each of its flushes to the disk counted here.</summary>
    /// <param name="journal">A new journal or one to continue, as <see cref="Journal.New"/> or <see cref="Journal.Continue"/> made it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is <see langword="null"/>.</exception>
    public Journal In(Journal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        return journal.WithWatchedFile(failureOf: _ => null, flushedToDisk: () => Interlocked.Increment(ref _count));
    }
}
