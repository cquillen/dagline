namespace Dagline.TestSupport;

/// <summary>
/// Reads a real journal file back as its entries in words, so that a test can assert which entries reached the file, and in what
/// order, without knowing the line format: <c>JournalEntries.Read(journalFile).ShouldBe(["header", "run started", "started load"])</c>.
/// </summary>
public static class JournalEntries
{
    /// <summary>
    /// Every whole line of the journal at <paramref name="journalFile"/>, which a run may still hold open, each in words as
    /// <c>header</c>, <c>run started</c>, <c>started load</c>, <c>settled load</c>, <c>set aside load</c>,
    /// <c>checkpoint saved load</c>, <c>checkpoint cleared load</c> or <c>run ended</c>. Only the kind of each entry and the
    /// task's path are given. A final line that a killed process left short is left out, as a run continuing the journal leaves it out.
    /// </summary>
    /// <param name="journalFile">The full path of the journal file.</param>
    public static IReadOnlyList<string> Read(string journalFile)
    {
        string text;
        using (var reading = new FileStream(journalFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(reading))
        {
            text = reader.ReadToEnd();
        }

        var wholeLines = text[..(text.LastIndexOf('\n') + 1)];
        return [.. wholeLines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JournalLine.Of(line).Describe())];
    }
}
