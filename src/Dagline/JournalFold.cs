namespace Dagline;

/// <summary>
/// What a journal's entries add up to, folded from the top: the journal itself, how many runs it has had, and for each path
/// what a later run needs. This is the one home of the reading rules, so a run and a listing read a journal alike.
/// </summary>
/// <remarks>
/// <para>
/// A path's status is its latest settle, or not succeeded once a later set-aside names it. A path the journal records only as
/// started, or not at all, has no settle.
/// </para>
/// <para>
/// A path's checkpoint is its latest save, and stands until a later clearing or a later settle that is a success.
/// </para>
/// <para>
/// A new rule of reading, such as a set-aside or a checkpoint, is a case in <see cref="Add"/> and what it keeps per path.
/// It is not thread-safe: it is folded once, before the run exists, and only read after that.
/// </para>
/// </remarks>
internal sealed class JournalFold
{
    private readonly Dictionary<string, TaskSettledEntry> _latestSettles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _setAside = new(StringComparer.Ordinal);
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _checkpoints = new(StringComparer.Ordinal);

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

    /// <summary>Every path the journal has an entry for, as the file spells it. Only a task's path is ever recorded.</summary>
    public IReadOnlyCollection<string> Paths => _paths;

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
    /// The latest settle of the task at <paramref name="path"/> when it is a success that has not been set aside since: an earlier
    /// success for a run that continues the journal. <see langword="null"/> when the path has no such settle.
    /// </summary>
    public TaskSettledEntry? EarlierSuccessAt(NodePath path) => EarlierSuccessAt(path.ToString());

    /// <inheritdoc cref="EarlierSuccessAt(NodePath)"/>
    public TaskSettledEntry? EarlierSuccessAt(string path)
        => !_setAside.Contains(path) && _latestSettles.TryGetValue(path, out var settled) && settled.Status is NodeStatus.Succeeded
            ? settled
            : null;

    /// <summary>
    /// The checkpoint of the task at <paramref name="path"/>: its latest save, unless it was cleared or the task succeeded since,
    /// whatever else is recorded in between. Empty when there is none.
    /// </summary>
    public string CheckpointAt(NodePath path) => _checkpoints.GetValueOrDefault(path.ToString(), "");

    private void Add(JournalEntry entry)
    {
        switch (entry)
        {
            case RunStartedEntry:
                Runs++;
                break;
            case TaskStartedEntry started:
                _paths.Add(started.Path);
                break;
            case TaskSettledEntry settled:
                _paths.Add(settled.Path);
                _latestSettles[settled.Path] = settled;
                _setAside.Remove(settled.Path);
                if (settled.Status is NodeStatus.Succeeded)
                {
                    _checkpoints.Remove(settled.Path);
                }

                break;
            case SetAsideEntry setAside:
                _paths.Add(setAside.Path);
                _setAside.Add(setAside.Path);
                break;
            case CheckpointSavedEntry saved:
                _checkpoints[saved.Path] = saved.Text;
                break;
            case CheckpointClearedEntry cleared:
                _checkpoints.Remove(cleared.Path);
                break;
        }
    }
}
