namespace Dagline;

/// <summary>
/// What a journal's entries add up to, folded from the top: the journal itself, how many runs it has had, and for each path
/// what a later run needs. This is the one home of the reading rules, so a run and a listing read a journal alike.
/// </summary>
/// <remarks>
/// <para>
/// A path's status is its latest settle. A path the journal records only as started, or not at all, has no settle.
/// </para>
/// <para>
/// A new rule of reading, such as a set-aside or a checkpoint, is a case in <see cref="Add"/> and what it keeps per path.
/// It is not thread-safe: it is folded once, before the run exists, and only read after that.
/// </para>
/// </remarks>
internal sealed class JournalFold
{
    private readonly Dictionary<string, TaskSettledEntry> _latestSettles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeStatus> _lastStatuses = new(StringComparer.Ordinal);
    private RunStartedEntry? _latestRunStarted;
    private RunEndedEntry? _latestRunEnded;

    private JournalFold(HeaderEntry header)
    {
        Id = header.Id;
        Created = header.Created;
        Description = header.Description;
    }

    /// <summary>The journal's id.</summary>
    public string Id { get; }

    /// <summary>When the journal was created.</summary>
    public DateTimeOffset Created { get; }

    /// <summary>The journal's description, or <see langword="null"/> for none.</summary>
    public string? Description { get; }

    /// <summary>How many runs the journal records as started.</summary>
    public int Runs { get; private set; }

    /// <summary>The journal as a snapshot of the run that continues it next shows it.</summary>
    public JournalInfo NextRun => new(Id, Description, Runs + 1);

    /// <summary>
    /// The latest run the journal records, or <see langword="null"/> when no run was ever recorded. Its end is
    /// <see langword="null"/> when the journal records none for it.
    /// </summary>
    public JournalRunSummary? LatestRun => _latestRunStarted is { } started
        ? new(started.Time, _latestRunEnded?.Time, started.User, started.Machine)
        : null;

    /// <summary>
    /// The run outcome the latest run recorded, or <see langword="null"/> when it recorded none, or no run was ever recorded.
    /// </summary>
    public RunOutcome? LatestOutcome => _latestRunEnded?.Outcome;

    /// <summary>
    /// How many paths have each status as their last recorded one, with every status present: the latest settle, or running when
    /// the path's latest entry is a start.
    /// </summary>
    public IReadOnlyDictionary<NodeStatus, int> LastStatusCounts()
    {
        Span<int> tally = stackalloc int[StatusCounts.TallyLength];
        foreach (var status in _lastStatuses.Values)
        {
            tally[(int)status]++;
        }

        return StatusCounts.ToDictionary(tally);
    }

    /// <summary>Folds the entries of one journal, in the order of the file, under its header.</summary>
    public static JournalFold Of(HeaderEntry header, IEnumerable<JournalEntry> entries)
    {
        var fold = new JournalFold(header);
        foreach (var entry in entries)
        {
            fold.Add(entry);
        }

        return fold;
    }

    /// <summary>
    /// The latest settle of the task at <paramref name="path"/> when it is a success: an earlier success for a run that continues
    /// the journal. <see langword="null"/> when the path has no settle or its latest is not a success.
    /// </summary>
    public TaskSettledEntry? EarlierSuccessAt(NodePath path)
        => _latestSettles.TryGetValue(path.ToString(), out var settled) && settled.Status is NodeStatus.Succeeded ? settled : null;

    private void Add(JournalEntry entry)
    {
        switch (entry)
        {
            case RunStartedEntry started:
                Runs++;
                _latestRunStarted = started;
                _latestRunEnded = null;
                break;
            case TaskStartedEntry started:
                _lastStatuses[started.Path] = NodeStatus.Running;
                break;
            case TaskSettledEntry settled:
                _latestSettles[settled.Path] = settled;
                _lastStatuses[settled.Path] = settled.Status;
                break;
            case RunEndedEntry ended:
                _latestRunEnded = ended;
                break;
        }
    }
}
