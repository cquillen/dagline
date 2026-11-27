namespace Dagline.TestSupport;

/// <summary>
/// Rewrites the header of a real journal file, so that a test can give it what an earlier Dagline could have written there
/// without knowing the line format: <c>JournalHeader.SetDescription(journalFile, "all\nshards")</c>.
/// </summary>
public static class JournalHeader
{
    /// <summary>
    /// Sets the description in the header of the journal at <paramref name="journalFile"/> to <paramref name="description"/>,
    /// exactly as given, line breaks included. The rest of the journal is left as it was. No run may hold the journal.
    /// </summary>
    /// <param name="journalFile">The full path of the journal file.</param>
    /// <param name="description">The description to write.</param>
    public static void SetDescription(string journalFile, string description)
    {
        var lines = File.ReadAllText(journalFile).Split('\n');
        lines[0] = JournalLine.WithDescription(lines[0], description);
        File.WriteAllText(journalFile, string.Join('\n', lines));
    }
}
