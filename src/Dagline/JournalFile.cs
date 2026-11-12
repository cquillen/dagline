using System.Text;
using System.Text.Json;

namespace Dagline;

/// <summary>
/// One journal's file, open for one run: the one type that knows the file format. It reads every entry when it is opened,
/// folds them, and from then on only appends.
/// </summary>
/// <remarks>
/// <para>
/// The file is UTF-8 text with one JSON object per line, each line ending in a line feed, appended only. The first line is a
/// <see cref="HeaderEntry"/> with the format version, which is <see cref="_version"/>.
/// </para>
/// <para>
/// A flush here reaches the disk: <see cref="FileStream.Flush(bool)"/> with <see langword="true"/>, which the asynchronous flush of a
/// file stream does not do. The stream has no buffer of its own, so each append reaches the operating system in one write
/// whether it is flushed or not, and appends land in the file in the order they were made.
/// </para>
/// <para>It is not thread-safe: the run appends under its lock.</para>
/// </remarks>
internal sealed class JournalFile : IDisposable
{
    private const int _version = 1;
    private const byte _lineFeed = (byte)'\n';

    private readonly FileStream _stream;

    private JournalFile(FileStream stream, JournalFold fold)
    {
        _stream = stream;
        Fold = fold;
    }

    /// <summary>What the journal held when it was opened.</summary>
    public JournalFold Fold { get; }

    /// <summary>
    /// Creates a new journal in <paramref name="directory"/>, creating the directory if it is missing, with an id from the time on
    /// <paramref name="clock"/>. The header is written and flushed before this returns. If a file with the id drawn already exists,
    /// a new suffix is drawn.
    /// </summary>
    public static JournalFile Create(string directory, string? description, TimeProvider clock)
    {
        Directory.CreateDirectory(directory);
        var created = clock.GetUtcNow();
        while (true)
        {
            var id = JournalId.New(created);
            var path = JournalId.FileIn(directory, id);
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
            }
            catch (IOException) when (File.Exists(path))
            {
                continue;
            }

            return OwningOnSuccess(stream, () =>
            {
                var header = new HeaderEntry(_version, id, created, description);
                var file = new JournalFile(stream, JournalFold.Of(header, []));
                file.Append([header], flush: true);
                return file;
            });
        }
    }

    /// <summary>Opens the journal <paramref name="id"/> in <paramref name="directory"/>, reads and folds it, and readies it for appending.</summary>
    /// <exception cref="JournalNotFoundException">The directory has no journal with that id.</exception>
    public static JournalFile Open(string directory, string id)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                JournalId.FileIn(directory, id),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 0);
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new JournalNotFoundException(directory, id, missing);
        }

        return OwningOnSuccess(stream, () =>
        {
            var fold = Read(stream);
            stream.Seek(0, SeekOrigin.End);
            return new JournalFile(stream, fold);
        });
    }

    /// <summary>
    /// Appends <paramref name="entries"/> in one write, one line each, and flushes them to the disk when <paramref name="flush"/>
    /// says so. Entries written together share one flush.
    /// </summary>
    public void Append(IReadOnlyList<JournalEntry> entries, bool flush)
    {
        using var lines = new MemoryStream();
        foreach (var entry in entries)
        {
            JsonSerializer.Serialize(lines, entry, JournalJson.Default.JournalEntry);
            lines.WriteByte(_lineFeed);
        }

        _stream.Write(lines.GetBuffer().AsSpan(0, (int)lines.Length));
        if (flush)
        {
            _stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Closes the file, so that a later run can continue the journal at once.</summary>
    public void Dispose() => _stream.Dispose();

    /// <summary>Gives the file to what <paramref name="open"/> makes of it, and closes the file when that throws.</summary>
    private static JournalFile OwningOnSuccess(FileStream stream, Func<JournalFile> open)
    {
        var owned = false;
        try
        {
            var file = open();
            owned = true;
            return file;
        }
        finally
        {
            if (!owned)
            {
                stream.Dispose();
            }
        }
    }

    private static JournalFold Read(FileStream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        var header = ReadEntry(reader.ReadLine()) as HeaderEntry
            ?? throw Unreadable("The file is not a journal: its first line is not a journal header.");
        if (header.Version != _version)
        {
            throw Unreadable($"The journal is in format version {header.Version}, which this Dagline cannot read.");
        }

        return JournalFold.Of(header, Entries(reader));
    }

    private static IEnumerable<JournalEntry> Entries(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return ReadEntry(line) ?? throw Unreadable("A line of the journal is not an entry.");
        }
    }

    // The one place a journal that cannot be read is reported. The typed exception for it comes with the errors of opening a journal.
    private static InvalidDataException Unreadable(string reason) => new(reason);

    private static JournalEntry? ReadEntry(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, JournalJson.Default.JournalEntry);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
