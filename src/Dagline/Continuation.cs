namespace Dagline;

/// <summary>
/// What a run takes from the journal it continues, decided once against the run's graph before the run starts: which tasks are
/// earlier successes, which earlier successes it sets aside, and how many journal paths it did not find in the graph.
/// </summary>
/// <remarks>
/// <para>
/// A task whose path the journal holds as succeeded keeps that success only if every node it is <em>after</em> is an earlier
/// success in this run: a task that kept its own, or a group whose every task beneath did, an empty group included. Otherwise the
/// success is set aside and the task runs again. A <em>requires</em> dependency never sets a success aside.
/// The rule is applied in dependency order, so a set-aside spreads through <em>after</em> dependencies and never through
/// <em>requires</em>.
/// </para>
/// <para>
/// A journal path that is not the path of a task of the graph, a group's included, is not part of the run and is only counted.
/// </para>
/// </remarks>
internal sealed class Continuation
{
    private readonly TaskSettledEntry?[] _kept;

    private Continuation(Graph graph, JournalFold? earlier)
    {
        _kept = new TaskSettledEntry?[graph.Nodes.Length];
        if (earlier is null)
        {
            return;
        }

        var keeps = new KeepRule(graph, earlier);
        foreach (var task in graph.Tasks)
        {
            if (earlier.EarlierSuccessAt(task.Path) is not { } recorded)
            {
                continue;
            }

            if (keeps.Keeps(task.Position))
            {
                _kept[task.Position] = recorded;
            }
            else
            {
                SetAside.Add(task);
            }
        }

        var taskPaths = graph.Tasks.Select(task => task.Path.ToString()).ToHashSet(StringComparer.Ordinal);
        var notInGraph = earlier.Paths.Where(path => !taskPaths.Contains(path)).ToList();
        PathsNotInGraph = notInGraph.Count;
        PathsNotInGraphUnsucceeded = notInGraph.Count(path => earlier.EarlierSuccessAt(path) is null);
    }

    /// <summary>The earlier successes this run sets aside, in declaration order.</summary>
    public List<BuiltTask> SetAside { get; } = [];

    /// <summary>How many paths the journal holds that are not the path of a task of the graph.</summary>
    public int PathsNotInGraph { get; }

    /// <summary>
    /// How many of the paths not in the graph have not succeeded: the journal holds no success for the path, or the success was set
    /// aside in an earlier run.
    /// </summary>
    public int PathsNotInGraphUnsucceeded { get; }

    /// <summary>What a run of <paramref name="graph"/> takes from <paramref name="earlier"/>, or nothing for a run with no journal.</summary>
    public static Continuation Of(Graph graph, JournalFold? earlier) => new(graph, earlier);

    /// <summary>The earlier success of <paramref name="task"/>, or <see langword="null"/> when it begins this run pending.</summary>
    public TaskSettledEntry? EarlierSuccessOf(BuiltTask task) => _kept[task.Position];

    /// <summary>
    /// Whether each node is an earlier success in this run, worked out in dependency order and kept, so each node is decided once.
    /// The walk keeps its own stack, since a long chain of <em>after</em> dependencies would be too deep for the call stack.
    /// </summary>
    private sealed class KeepRule
    {
        private readonly Graph _graph;
        private readonly JournalFold _earlier;
        private readonly bool?[] _keeps;
        private readonly List<int>[] _tasksBeneath;

        public KeepRule(Graph graph, JournalFold earlier)
        {
            _graph = graph;
            _earlier = earlier;
            _keeps = new bool?[graph.Nodes.Length];
            _tasksBeneath = [.. graph.Nodes.Select(_ => new List<int>())];
            foreach (var task in graph.Tasks)
            {
                foreach (var group in task.Groups)
                {
                    _tasksBeneath[group].Add(task.Position);
                }
            }
        }

        public bool Keeps(int position)
        {
            var unfinished = new Stack<(int Position, IEnumerator<int> Prerequisites)>();
            unfinished.Push((position, PrerequisitesOf(position).GetEnumerator()));
            while (unfinished.TryPeek(out var top))
            {
                if (_keeps[top.Position] is null && NextUndecided(top.Prerequisites) is { } next)
                {
                    unfinished.Push((next, PrerequisitesOf(next).GetEnumerator()));
                    continue;
                }

                _keeps[top.Position] ??= CouldKeep(top.Position)
                    && PrerequisitesOf(top.Position).All(prerequisite => _keeps[prerequisite] is true);
                unfinished.Pop();
            }

            return _keeps[position] is true;
        }

        private int? NextUndecided(IEnumerator<int> prerequisites)
        {
            while (prerequisites.MoveNext())
            {
                if (_keeps[prerequisites.Current] is null)
                {
                    return prerequisites.Current;
                }
            }

            return null;
        }

        /// <summary>Whether the node could be an earlier success at all: a group can, and a task can when it has a success on record.</summary>
        private bool CouldKeep(int position)
            => _graph.Nodes[position] is BuiltGroup || _earlier.EarlierSuccessAt(_graph.Nodes[position].Path) is not null;

        /// <summary>
        /// The nodes whose being kept decides whether this one is: for a group, every task beneath it; for a task with a success on
        /// record, what it is <em>after</em>. A task with no success on record needs nothing to be decided.
        /// </summary>
        private IEnumerable<int> PrerequisitesOf(int position)
            => _graph.Nodes[position] is BuiltGroup ? _tasksBeneath[position]
                : CouldKeep(position) ? _graph.TaskAt(position).Upstreams
                    .Where(upstream => upstream.Kind is DependencyKind.After)
                    .Select(upstream => upstream.Position)
                : [];
    }
}
