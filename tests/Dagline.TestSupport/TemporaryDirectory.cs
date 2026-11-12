namespace Dagline.TestSupport;

/// <summary>
/// A directory of its own for one test, made empty under the system's temporary directory and deleted with everything in it
/// when disposed. A journal test writes its journals here: <c>using var temporary = new TemporaryDirectory();</c>.
/// </summary>
public sealed class TemporaryDirectory : IDisposable
{
    /// <summary>Makes a new, empty directory with a name no other test uses.</summary>
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dagline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The full path of the directory.</summary>
    public string Path { get; }

    /// <summary>A path beneath the directory, which need not exist: <c>temporary.Combine("journals")</c>.</summary>
    /// <param name="relative">The path relative to the directory.</param>
    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    /// <summary>
    /// Copies a file into this directory under its own name, as it is at this instant, even while a run holds it open and writes to it.
    /// A test copies a journal mid-run this way to see what had reached the file by then.
    /// </summary>
    /// <param name="source">The full path of the file to copy.</param>
    /// <returns>The full path of the copy.</returns>
    public string CopyIn(string source)
    {
        var copy = Combine(System.IO.Path.GetFileName(source));
        using var reading = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var writing = new FileStream(copy, FileMode.Create, FileAccess.Write);
        reading.CopyTo(writing);
        return copy;
    }

    /// <summary>Deletes the directory and everything in it. A file another process still holds is left for the system to clear.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A run the test let go of may still hold a file for a moment. The system's temporary directory clears it later.
        }
        catch (UnauthorizedAccessException)
        {
            // As above, on Windows a file still open reads as access denied.
        }
    }
}
