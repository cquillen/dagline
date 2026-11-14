namespace Dagline;

/// <summary>
/// The app's choice of journal for a run: a new journal, a given journal to continue, or none. The choice is required,
/// so that running with no journal is visible in the code, and Dagline never makes it: not even when exactly one journal is unfinished.
/// </summary>
/// <remarks>
/// <para>
/// A choice only describes what the run will do. Nothing touches the disk until <see cref="Runner.RunAsync"/>, which opens or creates
/// the journal before the run exists.
/// </para>
/// <para>
/// There is no default directory, so that continuing never depends on where the command was launched. An app with more than one
/// kind of graph gives each kind a directory of its own.
/// </para>
/// <para>The set of choices is closed: a type outside Dagline cannot derive from this class.</para>
/// </remarks>
public abstract class Journal
{
    private protected Journal()
    {
    }

    /// <summary>No journal: the run records nothing, and a later run cannot continue it.</summary>
    public static Journal None { get; } = new NoJournal();

    /// <summary>
    /// A new journal in <paramref name="directory"/>, which a later run can continue.
    /// </summary>
    /// <param name="directory">
    /// The directory the journal goes in. <see cref="Runner.RunAsync"/> creates it, and any directory above it, if it does not exist.
    /// </param>
    /// <param name="description">
    /// Free text that tells this journal apart from others in the same directory. It is fixed at creation and never changes.
    /// <see langword="null"/> or empty means none.
    /// </param>
    /// <returns>The choice, to set as <see cref="RunOptions.Journal"/>.</returns>
    /// <remarks>
    /// <para>
    /// The run that creates the journal generates its id from the UTC time on <see cref="RunOptions.TimeProvider"/> as
    /// <c>yyyyMMddTHHmmssZ</c>, then a hyphen and six random lowercase hex digits, as in <c>20261111T041200Z-a3f9c2</c>.
    /// The id sorts by age, can be typed, and is the name of the journal's file, <c>&lt;id&gt;.journal</c>. When a file of that name
    /// already exists, a new suffix is drawn. <see cref="Snapshot.Journal"/> carries the id, from the first event of the run on.
    /// </para>
    /// <para>
    /// The journal's first line is written and flushed to the disk while it is created, before the run exists. Its lock file,
    /// <c>&lt;id&gt;.lock</c>, is created beside it. <see cref="Runner.RunAsync"/> throws <see cref="JournalAccessException"/> when the
    /// directory, the lock file or the journal cannot be created, or the first line cannot be written.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is empty or only white space.</exception>
    public static Journal New(string directory, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return new NewJournal(directory, string.IsNullOrEmpty(description) ? null : description);
    }

    /// <summary>
    /// The journal <paramref name="id"/> in <paramref name="directory"/>, for a run that continues it: a task the journal records as
    /// succeeded is an earlier success and does not run, unless its success is set aside, and every other task runs.
    /// </summary>
    /// <param name="directory">The directory the journal is in.</param>
    /// <param name="id">The journal's id, as <see cref="JournalInfo.Id"/> gave it.</param>
    /// <returns>The choice, to set as <see cref="RunOptions.Journal"/>.</returns>
    /// <remarks>
    /// <para>
    /// A task whose latest settle in the journal is a success, with no set-aside recorded since, is an <em>earlier success</em>
    /// unless this run sets it aside, as below: it is succeeded from the start of the run,
    /// with its message, detail lines and warnings and the time it succeeded, and no start time. It does not run, no event is
    /// published for it, and a dependency of either kind on it is met from the start. Every other task starts pending, whether it
    /// failed, was blocked or cancelled, or was never reached.
    /// </para>
    /// <para>
    /// A task that is <em>after</em> something keeps its earlier success only when everything it is after is an earlier success in
    /// this run. Otherwise its success is set aside: it starts pending and its body runs again, its path is in
    /// <see cref="Snapshot.SetAside"/>, and the journal records the set-aside before any task starts. The rule is applied in
    /// dependency order, so it spreads through <em>after</em> dependencies and never through <em>requires</em>. The bodies that run
    /// are those of every task that is not an earlier success, set-aside tasks included.
    /// </para>
    /// <para>
    /// The run may use a different <see cref="RunOptions.MaxRunningTasks"/> and different limit sizes from the runs before it. A journal whose latest run was complete
    /// can be continued like any other: with an unchanged graph the run is complete at once, and a path the graph gained runs.
    /// </para>
    /// <para>
    /// <see cref="Runner.RunAsync"/> throws <see cref="JournalNotFoundException"/> when the directory has no journal with the id,
    /// <see cref="JournalInUseException"/> when another run holds it, <see cref="JournalAccessException"/> when it cannot be opened or
    /// read, and <see cref="JournalFormatException"/> when it cannot be read as a journal. <see cref="Journals.ListAsync"/> lists the
    /// journals in a directory, to choose one from.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> or <paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="directory"/> is empty or only white space, or <paramref name="id"/> is not in the id format, so that an id can
    /// never name a file outside the directory.
    /// </exception>
    public static Journal Continue(string directory, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return new ContinuedJournal(JournalLocation.Of(directory, id));
    }

    /// <summary>Opens the journal the choice names, before the run exists: <see langword="null"/> for no journal.</summary>
    internal abstract JournalFile? Open(TimeProvider clock);

    private sealed class NoJournal : Journal
    {
        internal override JournalFile? Open(TimeProvider clock) => null;
    }

    private sealed class NewJournal(string directory, string? description) : Journal
    {
        internal override JournalFile Open(TimeProvider clock) => JournalFile.Create(directory, description, clock);
    }

    private sealed class ContinuedJournal(JournalLocation location) : Journal
    {
        internal override JournalFile Open(TimeProvider clock) => JournalFile.Open(location);
    }
}
