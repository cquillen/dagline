using System.Text;
using System.Text.Json;

namespace Dagline;

/// <summary>
/// One journal's file: the one type that knows the file format. Open for one run, it holds the journal, reads every entry when it
/// is opened, folds them, and from then on only appends. A listing reads a journal through it without holding it.
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
/// <para>
/// Opening checks in the order a run needs: the lock (<see cref="JournalLock"/>), then the file, then what it holds.
/// It is not thread-safe: the run appends under its lock.
/// </para>
/// </remarks>
internal sealed class JournalFile : IDisposable
{
    private const int _version = 1;
    private const byte _lineFeed = (byte)'\n';

    private static readonly UTF8Encoding _encoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FileStream _stream;
    private readonly JournalLock _claim;

    private JournalFile(ClaimedFile claimed, JournalFold fold)
    {
        _stream = claimed.Stream;
        _claim = claimed.Claim;
        Fold = fold;
    }

    /// <summary>What the journal held when it was opened.</summary>
    public JournalFold Fold { get; }

    /// <summary>
    /// Creates a new journal in <paramref name="directory"/>, creating the directory if it is missing, with an id from the time on
    /// <paramref name="clock"/>, and holds it. The header is written and flushed before this returns, and on Unix systems the
    /// directory's entries too, so that the new file's name survives a crash. If a file with the id drawn
    /// already exists, or another run has claimed that id, a new suffix is drawn.
    /// </summary>
    /// <exception cref="JournalOpenException">
    /// The directory, the lock file or the journal cannot be created, or the header cannot be written.
    /// </exception>
    public static JournalFile Create(string directory, string? description, TimeProvider clock)
    {
        CreateDirectory(directory);
        var created = clock.GetUtcNow();
        while (true)
        {
            var location = JournalLocation.New(directory, created);
            if (TryClaimNew(location) is { } claimed)
            {
                var header = new HeaderEntry(_version, location.Id, created, description);
                return OwningOnSuccess(claimed, () => WithHeader(claimed, header, location));
            }
        }
    }

    /// <summary>
    /// Claims the journal at <paramref name="location"/>, then opens it, reads and folds it, and readies it for appending.
    /// </summary>
    /// <exception cref="JournalNotFoundException">The directory has no journal with that id.</exception>
    /// <exception cref="JournalInUseException">Another run has claimed the journal.</exception>
    /// <exception cref="JournalOpenException">The lock file or the journal cannot be opened or read.</exception>
    /// <exception cref="JournalFormatException">The file cannot be read as a journal.</exception>
    public static JournalFile Open(JournalLocation location)
    {
        // Checked before the lock is taken, so that an id with no journal leaves no lock file behind.
        location.ThrowIfNotFound();

        var claimed = ClaimExisting(location);
        return OwningOnSuccess(claimed, () => ReadForAppending(claimed, location.FilePath));
    }

    /// <summary>
    /// Reads and folds the journal file at <paramref name="path"/> without holding it, as a listing does. A run may be appending
    /// to it meanwhile: what the run had written by then is read.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file is not there, or no longer.</exception>
    /// <exception cref="JournalOpenException">The file cannot be opened or read.</exception>
    /// <exception cref="JournalFormatException">The file cannot be read as a journal.</exception>
    public static async Task<JournalFold> ReadAsync(string path, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            // Shared for delete as well, so that a listing never stands in the way of deleting a journal.
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            using var reader = new StreamReader(stream, _encoding);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cannotOpen) when (FileSystemFailure.Is(cannotOpen) && cannotOpen is not FileNotFoundException)
        {
            throw new JournalOpenException(path, cannotOpen);
        }

        using var lines = new StringReader(WholeLines(text));
        return Read(lines, path);
    }

    /// <summary>
    /// Appends <paramref name="entries"/> in one write, one line each, and flushes them to the disk when <paramref name="flush"/>
    /// says so. Entries written together share one flush.
    /// </summary>
    public void Append(IReadOnlyList<JournalEntry> entries, JournalFlush flush)
    {
        using var lines = new MemoryStream();
        foreach (var entry in entries)
        {
            JsonSerializer.Serialize(lines, entry, JournalJson.Default.JournalEntry);
            lines.WriteByte(_lineFeed);
        }

        _stream.Write(lines.GetBuffer().AsSpan(0, (int)lines.Length));
        if (flush is JournalFlush.ToDisk)
        {
            _stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Closes the file and lets go of the journal, so that a later run can continue it at once.</summary>
    public void Dispose()
    {
        _stream.Dispose();
        _claim.Dispose();
    }

    private static void CreateDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception cannotCreate) when (FileSystemFailure.Is(cannotCreate))
        {
            throw new JournalOpenException(directory, cannotCreate);
        }
    }

    /// <summary>
    /// Claims a new journal at <paramref name="location"/> and creates its file, or gives <see langword="null"/> when the id drawn is
    /// taken: another run has claimed it, or a file of that name exists.
    /// </summary>
    /// <exception cref="JournalOpenException">The lock file or the journal cannot be created.</exception>
    private static ClaimedFile? TryClaimNew(JournalLocation location)
    {
        JournalLock claim;
        try
        {
            claim = JournalLock.Claim(location);
        }
        catch (JournalInUseException)
        {
            return null;
        }

        try
        {
            return new ClaimedFile(
                new FileStream(location.FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0),
                claim);
        }
        catch (IOException) when (File.Exists(location.FilePath))
        {
            claim.Dispose();
            return null;
        }
        catch (Exception cannotCreate) when (FileSystemFailure.Is(cannotCreate))
        {
            claim.Dispose();
            throw new JournalOpenException(location.FilePath, cannotCreate);
        }
    }

    /// <summary>
    /// A new journal: writes <paramref name="header"/> and flushes it to the disk, then, on a Unix system, the directory's entries,
    /// so that the new file's name survives a crash.
    /// </summary>
    /// <exception cref="JournalOpenException">The header or the directory's entries cannot be written.</exception>
    private static JournalFile WithHeader(ClaimedFile claimed, HeaderEntry header, JournalLocation location)
    {
        var file = new JournalFile(claimed, JournalFold.Of(header, []));
        try
        {
            file.Append([header], JournalFlush.ToDisk);
            DirectoryEntries.Flush(location.Directory);
        }
        catch (Exception cannotWrite) when (FileSystemFailure.Is(cannotWrite))
        {
            throw new JournalOpenException(location.FilePath, cannotWrite);
        }

        return file;
    }

    /// <summary>Claims the journal at <paramref name="location"/> and opens its file.</summary>
    /// <exception cref="JournalNotFoundException">The file is gone since it was found.</exception>
    /// <exception cref="JournalInUseException">Another run has claimed the journal.</exception>
    /// <exception cref="JournalOpenException">The lock file or the journal cannot be opened.</exception>
    private static ClaimedFile ClaimExisting(JournalLocation location)
    {
        var claim = JournalLock.Claim(location);
        try
        {
            return new ClaimedFile(
                new FileStream(location.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0),
                claim);
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            claim.Dispose();
            throw new JournalNotFoundException(location, missing);
        }
        catch (Exception cannotOpen) when (FileSystemFailure.Is(cannotOpen))
        {
            claim.Dispose();
            throw new JournalOpenException(location.FilePath, cannotOpen);
        }
    }

    /// <summary>
    /// Reads and folds the whole lines of a journal just opened, cuts off a final line a killed process left short, and moves to
    /// the end for appending.
    /// </summary>
    /// <exception cref="JournalOpenException">The file cannot be read.</exception>
    /// <exception cref="JournalFormatException">The file cannot be read as a journal.</exception>
    private static JournalFile ReadForAppending(ClaimedFile claimed, string path)
    {
        var stream = claimed.Stream;
        try
        {
            using var reader = new StreamReader(stream, _encoding, leaveOpen: true);
            var whole = WholeLines(reader.ReadToEnd());
            var fold = Read(new StringReader(whole), path);
            stream.SetLength(_encoding.GetByteCount(whole));
            stream.Seek(0, SeekOrigin.End);
            return new JournalFile(claimed, fold);
        }
        catch (Exception cannotRead) when (FileSystemFailure.Is(cannotRead))
        {
            throw new JournalOpenException(path, cannotRead);
        }
    }

    /// <summary>
    /// Gives the file and the claim on the journal to what <paramref name="open"/> makes of them, and closes the file and lets go of
    /// the claim when that throws.
    /// </summary>
    private static JournalFile OwningOnSuccess(ClaimedFile claimed, Func<JournalFile> open)
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
                claimed.Stream.Dispose();
                claimed.Claim.Dispose();
            }
        }
    }

    /// <summary>
    /// The text of a journal up to the end of its last whole line. A line is whole once its line feed is written, so text after the
    /// last line feed is a final line that a killed process cut short, and is dropped.
    /// </summary>
    private static string WholeLines(string text) => text[..(text.LastIndexOf((char)_lineFeed) + 1)];

    private static JournalFold Read(TextReader reader, string path)
    {
        var header = ReadEntry(reader.ReadLine()) as HeaderEntry
            ?? throw new JournalFormatException(path, "its first line is not a journal header.");
        if (header.Version != _version)
        {
            throw new JournalFormatException(
                path,
                $"it is in format version {header.Version}, which this Dagline cannot read.",
                header.Version);
        }

        return JournalFold.Of(header, Entries(reader, path, header.Version));
    }

    private static IEnumerable<JournalEntry> Entries(TextReader reader, string path, int version)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return ReadEntry(line) ?? throw new JournalFormatException(path, "a line of it is not an entry.", version);
        }
    }

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

    /// <summary>A journal's file, open, with the claim on the journal taken before it was opened.</summary>
    private readonly record struct ClaimedFile(FileStream Stream, JournalLock Claim);
}
