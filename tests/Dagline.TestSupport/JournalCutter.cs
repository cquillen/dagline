namespace Dagline.TestSupport;

/// <summary>
/// Simulates a killed process on a real journal: copies the journal of a run and cuts the copy short, at every line boundary and once
/// inside every line, the way a process killed in the middle of a write could leave it.
/// <c>foreach (var cut in JournalCutter.Cut(journalFile)) { using var copy = new TemporaryDirectory(); cut.WriteTo(copy.Path); ... }</c>.
/// </summary>
/// <remarks>
/// The header line is never cut: it is flushed when the journal is created, before the run that created it exists, so a killed run
/// always leaves it whole. Cuts come in the order of the file, each boundary just before the cut inside the line that follows it.
/// </remarks>
public static class JournalCutter
{
    /// <summary>Every cut of the journal at <paramref name="journalFile"/>, which a run may still hold open.</summary>
    /// <param name="journalFile">The full path of the journal file.</param>
    public static IReadOnlyList<JournalCut> Cut(string journalFile)
    {
        byte[] bytes;
        using (var reading = new FileStream(journalFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var copy = new MemoryStream())
        {
            reading.CopyTo(copy);
            bytes = copy.ToArray();
        }

        var lineEnds = new List<int>();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                lineEnds.Add(i + 1);
            }
        }

        var name = Path.GetFileName(journalFile);
        var cuts = new List<JournalCut>();
        for (var line = 1; line <= lineEnds.Count; line++)
        {
            var end = lineEnds[line - 1];
            cuts.Add(new JournalCut(name, bytes.AsMemory(0, end), line, InsideALine: false));
            if (line < lineEnds.Count)
            {
                var halfOfNext = end + ((lineEnds[line] - end) / 2);
                cuts.Add(new JournalCut(name, bytes.AsMemory(0, halfOfNext), line, InsideALine: true));
            }
        }

        return cuts;
    }
}

/// <summary>One cut of a journal, as <see cref="JournalCutter.Cut"/> made it.</summary>
/// <param name="FileName">The journal's file name, which names its id.</param>
/// <param name="Bytes">What is left of the file.</param>
/// <param name="WholeLines">How many lines are left whole, the header included.</param>
/// <param name="InsideALine">Whether part of the next line follows the whole lines.</param>
public sealed record JournalCut(string FileName, ReadOnlyMemory<byte> Bytes, int WholeLines, bool InsideALine)
{
    /// <summary>Writes the cut journal into <paramref name="directory"/> under its own name, so a run can continue it there.</summary>
    /// <param name="directory">The directory to write into.</param>
    /// <returns>The full path of the cut journal.</returns>
    public string WriteTo(string directory)
    {
        var path = Path.Combine(directory, FileName);
        File.WriteAllBytes(path, Bytes.Span);
        return path;
    }

    /// <inheritdoc />
    public override string ToString() => InsideALine ? $"{WholeLines} whole lines and part of the next" : $"{WholeLines} whole lines";
}
