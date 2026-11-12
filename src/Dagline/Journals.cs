namespace Dagline;

/// <summary>
/// The journals in a directory, for an app to manage: list them, with enough about each to choose one, and delete one. Neither
/// needs a graph.
/// </summary>
/// <remarks>
/// Dagline never deletes or expires a journal itself: how long a record is kept is the app's policy. There is no API for the
/// per-task detail of a past journal.
/// </remarks>
public static class Journals
{
    /// <summary>Summarises every journal in <paramref name="directory"/>, oldest first.</summary>
    /// <param name="directory">The directory the journals are in, as given to <see cref="Journal.New"/>.</param>
    /// <param name="cancellationToken">Cancels the listing between and during the reading of files.</param>
    /// <returns>
    /// One <see cref="JournalSummary"/> for each journal, in order of creation, oldest first, and by id among journals created in the
    /// same second. A directory that does not exist gives an empty list.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A journal is a file named <c>&lt;id&gt;.journal</c> with an id in the id format. Every other file is left out. Each journal is
    /// read in full, so listing a directory of large journals takes a while.
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
    /// <exception cref="JournalFormatException">
    /// A journal cannot be read: it is in a format version this Dagline does not know, or is not a journal. The message names its
    /// file. The listing does not leave it out, so that no journal is ever missing from it unseen.
    /// </exception>
    /// <exception cref="JournalOpenException">A journal's file or the directory cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<IReadOnlyList<JournalSummary>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var summaries = new List<JournalSummary>();
        foreach (var location in JournalsIn(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            JournalFold fold;
            try
            {
                fold = await JournalFile.ReadAsync(location.FilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            summaries.Add(new JournalSummary(fold, JournalLock.IsInUse(location)));
        }

        return [.. summaries.OrderBy(summary => summary.Created).ThenBy(summary => summary.Id, StringComparer.Ordinal)];
    }

    /// <summary>Deletes the journal <paramref name="id"/> in <paramref name="directory"/>, and its lock file.</summary>
    /// <param name="directory">The directory the journal is in.</param>
    /// <param name="id">The journal's id, as <see cref="JournalSummary.Id"/> or <see cref="JournalInfo.Id"/> gave it.</param>
    /// <param name="cancellationToken">Cancels the deletion before it begins. Once a file has been removed it is not put back.</param>
    /// <returns>A task that completes once both files are gone.</returns>
    /// <remarks>
    /// The journal is held while it is deleted, so a run can never start on it halfway, and a run never has it pulled from under it.
    /// A lock file left behind without a holder, as every run that has ended leaves one, does not stand in the way.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> or <paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="directory"/> is empty or only white space, or <paramref name="id"/> is not in the id format, so that an id can
    /// never name a file outside the directory.
    /// </exception>
    /// <exception cref="JournalInUseException">A run holds the journal.</exception>
    /// <exception cref="JournalNotFoundException">The directory has no journal with the id.</exception>
    /// <exception cref="JournalOpenException">The lock file or the journal cannot be opened or removed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task DeleteAsync(string directory, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var location = JournalLocation.Of(directory, id);
        cancellationToken.ThrowIfCancellationRequested();

        location.ThrowIfNotFound();
        using var claim = JournalLock.ClaimToDelete(location);
        try
        {
            File.Delete(location.FilePath);
        }
        catch (Exception cannotDelete) when (FileSystemFailure.Is(cannotDelete))
        {
            throw new JournalOpenException(location.FilePath, cannotDelete);
        }

        return Task.CompletedTask;
    }

    private static List<JournalLocation> JournalsIn(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(directory, "*" + JournalId.Extension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(JournalId.IsValid)
                .Select(id => JournalLocation.Of(directory, id))
                .ToList();
        }
        catch (Exception cannotList) when (FileSystemFailure.Is(cannotList))
        {
            throw new JournalOpenException(directory, cannotList);
        }
    }
}
