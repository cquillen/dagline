namespace Dagline.Prototype;

/// <summary>What the loop writes journal entries to. The loop calls it, one write at a time, and hands the result to the core.</summary>
internal interface IJournalWriter
{
    /// <returns><see langword="null"/> when the write landed, and otherwise why it did not.</returns>
    Exception? TryWrite(IReadOnlyList<JournalEntry> entries, JournalFlush flush);

    void Close();
}

/// <summary>A journal kept in memory, whose writes a test can make fail.</summary>
internal sealed class MemoryJournal : IJournalWriter
{
    /// <summary>Every write that landed, in order, each with the entries it wrote together.</summary>
    public List<(IReadOnlyList<JournalEntry> Entries, JournalFlush Flush)> Writes { get; } = [];

    public IEnumerable<JournalEntry> Entries => Writes.SelectMany(write => write.Entries);

    /// <summary>Says whether a write is to fail, given its entries. Every write is asked, in order.</summary>
    public Func<IReadOnlyList<JournalEntry>, bool> FailsWhen { get; set; } = _ => false;

    public int FailedWrites { get; private set; }

    public bool IsClosed { get; private set; }

    public Exception? TryWrite(IReadOnlyList<JournalEntry> entries, JournalFlush flush)
    {
        if (IsClosed)
        {
            throw new InvalidOperationException("The journal was written to after it was closed.");
        }

        if (FailsWhen(entries))
        {
            FailedWrites++;
            return new IOException("The disk is full.");
        }

        Writes.Add(([.. entries], flush));
        return null;
    }

    public void Close() => IsClosed = true;

    public string Describe() => string.Join("; ", Entries.Select(Describe));

    public static string Describe(JournalEntry entry) => entry switch
    {
        RunStartedEntry => "run started",
        TaskStartedEntry started => $"{started.Path} started",
        TaskSettledEntry settled => $"{settled.Path} {settled.Status.ToString().ToLowerInvariant()}",
        CheckpointSavedEntry saved => $"{saved.Path} checkpoint '{saved.Text}'",
        CheckpointClearedEntry cleared => $"{cleared.Path} checkpoint cleared",
        RunEndedEntry ended => $"run ended {ProtoOutcome.Name(ended.Outcome).ToLowerInvariant()}",
        _ => entry.ToString(),
    };
}

/// <summary>A real journal file, written through Dagline's own JournalFile: a synchronous write and flush on the loop's thread.</summary>
internal sealed class FileJournal(JournalFile file) : IJournalWriter
{
    public static FileJournal New(string directory, TimeProvider clock) => new(Journal.New(directory).Open(clock)!);

    public Exception? TryWrite(IReadOnlyList<JournalEntry> entries, JournalFlush flush)
    {
        try
        {
            file.Append(entries, flush);
            return null;
        }
        catch (Exception cause) when (FileSystemFailure.Is(cause))
        {
            return cause;
        }
    }

    public void Close() => file.Dispose();
}
