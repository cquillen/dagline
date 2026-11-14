using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// The journals in a directory, for an app to manage: list them, with enough about each to choose one, check an id, and delete
/// one. None of these needs a graph.
/// </summary>
/// <remarks>
/// Dagline never deletes or expires a journal itself: how long a record is kept is the app's policy. There is no API for the
/// per-task detail of a past journal.
/// </remarks>
public static class Journals
{
    /// <summary>Summarises every journal in <paramref name="directory"/>, oldest first, and names each one that cannot be read.</summary>
    /// <param name="directory">The directory the journals are in, as given to <see cref="Journal.New"/>.</param>
    /// <param name="cancellationToken">Cancels the listing between and during the reading of files.</param>
    /// <returns>
    /// A <see cref="JournalListing"/>: in <see cref="JournalListing.Journals"/>, one <see cref="JournalSummary"/> for each journal
    /// that could be read, in order of creation, oldest first, and by id among journals created in the same second; in
    /// <see cref="JournalListing.Unreadable"/>, each journal file that could not be read, with the reason. A directory that does not
    /// exist gives two empty lists.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A journal is a file named <c>&lt;id&gt;.journal</c> with an id in the id format. Every other file is left out. Each journal is
    /// read in full, so listing a directory of large journals takes a while.
    /// </para>
    /// <para>
    /// A journal that cannot be read does not stop the listing: one in a format version this Dagline does not know, one that is not
    /// a journal, or one whose file or lock file cannot be opened or read, is listed in <see cref="JournalListing.Unreadable"/> with
    /// its <see cref="JournalFormatException"/> or <see cref="JournalAccessException"/>, and the others are read as usual. It is never
    /// left out, so that no journal is ever missing from the listing unseen.
    /// </para>
    /// <para>
    /// A run may hold a journal while it is listed: its summary shows what the run had written by then, and its ending is
    /// <see cref="JournalEnding.InUse"/>. To tell whether a run holds it, the listing takes the journal's lock for a moment, so a run
    /// that starts on that journal at the same instant may throw <see cref="JournalInUseException"/>. A journal deleted while the
    /// listing runs is left out.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is empty or only white space.</exception>
    /// <exception cref="JournalAccessException">
    /// The directory itself cannot be read, for example for lack of permission or because the path names a file. Its
    /// <see cref="JournalAccessException.Id"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<JournalListing> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        cancellationToken.ThrowIfCancellationRequested();
        var summaries = new List<JournalSummary>();
        var unreadable = new List<UnreadableJournal>();
        foreach (var location in JournalsIn(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fold = await JournalFile.ReadAsync(location, cancellationToken).ConfigureAwait(false);
                summaries.Add(new JournalSummary(fold, JournalLock.IsInUse(location)));
            }
            catch (FileNotFoundException)
            {
                // Deleted since the directory was listed.
            }
            catch (DaglineException cannotRead) when (cannotRead is JournalFormatException or JournalAccessException)
            {
                unreadable.Add(new UnreadableJournal(location.FilePath, cannotRead));
            }
        }

        return new JournalListing(
            [.. summaries.OrderBy(summary => summary.Created).ThenBy(summary => summary.Id, StringComparer.Ordinal)],
            [.. unreadable.OrderBy(journal => journal.FilePath, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Whether <paramref name="id"/> is in the journal id format: the UTC creation time as <c>yyyyMMddTHHmmssZ</c>, a hyphen and six
    /// lowercase hex digits, as in <c>20261111T041200Z-a3f9c2</c>. It never throws.
    /// </summary>
    /// <param name="id">The text to check, such as an id an operator typed. It may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="id"/> is in the id format, so that <see cref="Journal.Continue"/> and
    /// <see cref="DeleteAsync"/> accept it; <see langword="false"/> for <see langword="null"/> and for any other text, which they
    /// refuse with <see cref="ArgumentException"/>. It says nothing about whether a journal with the id exists.
    /// </returns>
    public static bool IsValidId([NotNullWhen(true)] string? id) => id is not null && JournalId.IsValid(id);

    /// <summary>Deletes the journal <paramref name="id"/> in <paramref name="directory"/>, and its lock file.</summary>
    /// <param name="directory">The directory the journal is in.</param>
    /// <param name="id">The journal's id, as <see cref="JournalSummary.Id"/> or <see cref="JournalInfo.Id"/> gave it.</param>
    /// <param name="cancellationToken">Cancels the deletion before it begins. Once a file has been removed it is not put back.</param>
    /// <returns>
    /// A task that completes once both files are gone. A failure to delete, and a cancellation, are reported through this task.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The journal is held while it is deleted, so a run can never start on it halfway, and a run never has it pulled from under it.
    /// A lock file left behind without a holder, as every run that has ended leaves one, does not stand in the way.
    /// </para>
    /// <para>
    /// The arguments are checked by the call itself, which throws before any task is returned. Every other exception listed here
    /// faults the returned task, and a cancellation cancels it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="directory"/> or <paramref name="id"/> is <see langword="null"/>. It is thrown by the call itself.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="directory"/> is empty or only white space, or <paramref name="id"/> is not in the id format, so that an id can
    /// never name a file outside the directory. It is thrown by the call itself.
    /// </exception>
    /// <exception cref="JournalInUseException">A run holds the journal.</exception>
    /// <exception cref="JournalNotFoundException">The directory has no journal with the id.</exception>
    /// <exception cref="JournalAccessException">The lock file or the journal cannot be opened or removed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled before the deletion began. Nothing is deleted.
    /// </exception>
    public static Task DeleteAsync(string directory, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var location = JournalLocation.Of(directory, id);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        try
        {
            Delete(location);
            return Task.CompletedTask;
        }
        catch (Exception failed) when (failed is JournalNotFoundException or JournalInUseException or JournalAccessException)
        {
            return Task.FromException(failed);
        }
    }

    /// <summary>Deletes the journal at <paramref name="location"/> and its lock file, claiming the journal while it does.</summary>
    /// <exception cref="JournalNotFoundException">The directory has no journal with the id.</exception>
    /// <exception cref="JournalInUseException">A run holds the journal.</exception>
    /// <exception cref="JournalAccessException">The lock file or the journal cannot be opened or removed.</exception>
    private static void Delete(JournalLocation location)
    {
        location.ThrowIfNotFound();
        using var claim = JournalLock.ClaimToDelete(location);
        try
        {
            File.Delete(location.FilePath);
        }
        catch (Exception cannotDelete) when (FileSystemFailure.Is(cannotDelete))
        {
            throw JournalAccessException.FileNotDeleted(location, cannotDelete);
        }
    }

    private static List<JournalLocation> JournalsIn(string directory)
    {
        // Not Directory.Exists, which also says false for a directory the process may not inspect.
        try
        {
            return Directory.GetFiles(directory, "*" + JournalId.Extension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(JournalId.IsValid)
                .Select(id => JournalLocation.Of(directory, id))
                .ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception cannotList) when (FileSystemFailure.Is(cannotList))
        {
            throw JournalAccessException.DirectoryNotListed(directory, cannotList);
        }
    }
}
