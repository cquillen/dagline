namespace Dagline;

/// <summary>
/// The ready tasks of one run that a full limit has turned away, kept so that a walk tries one only when it fits, and in
/// declaration order. <see cref="RunSteps"/> merges them, by position, with the ready tasks no limit has turned away.
/// </summary>
/// <remarks>
/// <para>
/// Held tasks are kept together by the set of limits they hold, in declaration order, since tasks that hold the same limits
/// fit or do not fit together. Each set that holds a task waits under one full limit, queued by the position of its first
/// task. A limit is releasing from a task giving back one of its slots until a walk finds it full again, and while it is, its
/// queue offers its first set's first task, merged with the other releasing limits' by position. A set offered while a second
/// limit of it is full moves to that limit's queue, and the set behind it is offered next. Only a task whose limits all have
/// room is handed out to be tried, so a held task is tried only when no limit can turn it away. A walk cut short by the max
/// running tasks leaves its limits releasing, so the sets it never reached are offered at the next walk, whichever limits the
/// settle before it gives back.
/// </para>
/// <para>
/// Releasing every task held by a limit at each give-back made each walk try nearly all of them to start one, so a run's slot
/// tries grew with the square of its tasks. Keeping held tasks under a limit one by one, rather than by their set of limits,
/// moved a task holding two full limits from one to the other at every settle of either, at the cost of a try each time.
/// </para>
/// <para>
/// It is not thread-safe: <see cref="RunSteps"/> owns it and uses it one step at a time.
/// </para>
/// </remarks>
internal sealed class HeldTasks
{
    private readonly Slots _slots;
    private readonly int[] _setOf;
    private readonly int[][] _limitsOf;
    private readonly SortedSet<int>[] _tasksOf;

    // For each limit, the sets waiting under it, by the position of each set's first task.
    private readonly SortedSet<(int Position, int Set)>[] _waitingUnder;
    private readonly int[] _waitsUnder;
    private readonly bool[] _isReleasing;
    private readonly List<int> _releasing = [];

    // The releasing limits, by the position of their first waiting task. Filled at the start of each walk and reused.
    private readonly PriorityQueue<int, int> _offered = new();

    /// <param name="graph">The graph the run runs.</param>
    /// <param name="slots">The slots of the run, which say whether a limit is full.</param>
    public HeldTasks(Graph graph, Slots slots)
    {
        _slots = slots;
        _setOf = new int[graph.Nodes.Length];
        Dictionary<string, int> sets = [];
        List<int[]> limitsOf = [];
        foreach (var task in graph.Tasks)
        {
            int[] limits = [.. task.HeldLimits.Distinct().Order()];
            var key = string.Join(',', limits);
            if (!sets.TryGetValue(key, out var set))
            {
                set = limitsOf.Count;
                sets.Add(key, set);
                limitsOf.Add(limits);
            }

            _setOf[task.Position] = set;
        }

        _limitsOf = [.. limitsOf];
        _tasksOf = [.. _limitsOf.Select(_ => new SortedSet<int>())];
        _waitsUnder = new int[_limitsOf.Length];
        _waitingUnder = [.. graph.LimitNames.Select(_ => new SortedSet<(int Position, int Set)>())];
        _isReleasing = new bool[graph.LimitNames.Length];
    }

    /// <summary>
    /// A full limit has turned away a ready task: it is held until every limit it holds has room. A task whose set already holds
    /// tasks joins them where they wait.
    /// </summary>
    /// <param name="task">The task turned away.</param>
    /// <param name="fullLimit">A full limit of the task, as its position among <see cref="Graph.LimitNames"/>.</param>
    public void Hold(BuiltTask task, int fullLimit)
    {
        var set = _setOf[task.Position];
        var tasks = _tasksOf[set];
        if (tasks.Count == 0)
        {
            tasks.Add(task.Position);
            Wait(set, fullLimit);
            return;
        }

        var under = _waitsUnder[set];
        _waitingUnder[under].Remove((tasks.Min, set));
        tasks.Add(task.Position);
        _waitingUnder[under].Add((tasks.Min, set));
    }

    /// <summary>A task has given back a slot of a limit: the sets waiting under it may fit again.</summary>
    /// <param name="limit">The limit, as its position among <see cref="Graph.LimitNames"/>.</param>
    public void Release(int limit)
    {
        if (!_isReleasing[limit] && _waitingUnder[limit].Count > 0)
        {
            _isReleasing[limit] = true;
            _releasing.Add(limit);
        }
    }

    /// <summary>Begins a walk: from now on <see cref="First"/> offers the waiting sets of each releasing limit.</summary>
    public void BeginWalk()
    {
        _offered.Clear();
        foreach (var limit in _releasing)
        {
            Offer(limit);
        }
    }

    /// <summary>
    /// The position of the first held task, among those the releasing limits offer, whose limits all have room, or
    /// <see langword="null"/> when there is none. On the way, a releasing limit found full stops releasing, and a set offered
    /// with another limit full moves to wait under that one; neither is tried.
    /// </summary>
    public int? First()
    {
        while (_offered.TryPeek(out var limit, out _))
        {
            if (!_isReleasing[limit] || _waitingUnder[limit].Count == 0)
            {
                _offered.Dequeue();
                continue;
            }

            var (position, set) = _waitingUnder[limit].Min;
            switch (_slots.FullLimitAmong(_limitsOf[set]))
            {
                case null:
                    return position;
                case var full when full == limit:
                    _isReleasing[limit] = false;
                    _offered.Dequeue();
                    break;
                case { } full:
                    _waitingUnder[limit].Remove((position, set));
                    Wait(set, full);
                    _offered.Dequeue();
                    Offer(limit);
                    break;
            }
        }

        return null;
    }

    /// <summary>The task <see cref="First"/> gave has started: it is no longer held, and the task behind it is offered.</summary>
    public void StartedFirst()
    {
        var limit = _offered.Dequeue();
        var (position, set) = _waitingUnder[limit].Min;
        _waitingUnder[limit].Remove((position, set));
        var tasks = _tasksOf[set];
        tasks.Remove(position);
        if (tasks.Count > 0)
        {
            _waitingUnder[limit].Add((tasks.Min, set));
        }

        Offer(limit);
    }

    /// <summary>Ends a walk: a limit stays releasing only while sets wait under it, so that the next walk offers them.</summary>
    public void EndWalk()
    {
        foreach (var limit in _releasing)
        {
            if (_waitingUnder[limit].Count == 0)
            {
                _isReleasing[limit] = false;
            }
        }

        _releasing.RemoveAll(limit => !_isReleasing[limit]);
    }

    private void Wait(int set, int limit)
    {
        _waitsUnder[set] = limit;
        _waitingUnder[limit].Add((_tasksOf[set].Min, set));
    }

    private void Offer(int limit)
    {
        if (_isReleasing[limit] && _waitingUnder[limit].Count > 0)
        {
            _offered.Enqueue(limit, _waitingUnder[limit].Min.Position);
        }
    }
}
