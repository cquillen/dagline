namespace Dagline.TestSupport;

/// <summary>
/// Journal choices <see cref="JournalDurability.HandedToOperatingSystem"/>, for a test that runs on a real journal but is not about
/// what survives a power cut: <c>QuickJournal.New(temporary.Path)</c>.
/// </summary>
/// <remarks>
/// Every entry reaches the file at the same moments as with the default, so what the test reads back, lists or continues is the
/// same, but no flush to the disk is made. A test that repeats runs on a journal makes hundreds of them otherwise, and on a slow
/// disk is bound by the disk (see <see cref="DiskHeavy"/>).
/// </remarks>
public static class QuickJournal
{
    /// <summary>A new journal in <paramref name="directory"/>, as <see cref="Journal.New"/> makes it, handed to the operating system only.</summary>
    /// <param name="directory">The directory the journal goes in.</param>
    public static Journal New(string directory) => Journal.New(directory, durability: JournalDurability.HandedToOperatingSystem);

    /// <summary>The journal <paramref name="id"/> in <paramref name="directory"/>, to continue, handed to the operating system only.</summary>
    /// <param name="directory">The directory the journal is in.</param>
    /// <param name="id">The journal's id.</param>
    public static Journal Continue(string directory, string id)
        => Journal.Continue(directory, id, JournalDurability.HandedToOperatingSystem);
}
