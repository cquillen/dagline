namespace Dagline;

/// <summary>
/// The ready tasks of one run that a full limit has turned away, kept so that a walk tries one only when it fits, and in
/// declaration order. <see cref="RunSteps"/> merges them, by position, with the ready tasks no limit has turned away.
/// </summary>
/// <remarks>
/// <para>
/// The held tasks are kept apart by the set of limits they hold, each set in declaration order, since tasks holding the same
/// limits fit or do not fit together. A set is woken when a task gives back a slot of one of its limits, and sleeps again
/// once one of its limits is found full. Only a woken set whose limits all have room offers its first task, so a held task
/// is tried only when no limit can turn it away. A walk cut short by the max running tasks leaves its sets woken, so the
/// tasks it never reached are offered at the next walk, whichever limits the settle before it gives back.
/// </para>
/// <para>
/// Releasing every task held by a limit at each give-back made each walk try nearly all of them to start one, so a run's slot
/// tries grew with the square of its tasks. A task holding two full limits, moved from one to the other at each give-back,
/// would cost a try at every settle of either.
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
    private readonly List<int>[] _setsHolding;
    private readonly SortedSet<int>[] _held;
    private readonly bool[] _isWoken;
    private readonly List<int> _woken = [];

    // The first task of each woken set, by position. Filled at the start of each walk and reused.
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
        _setsHolding = [.. graph.LimitNames.Select(_ => new List<int>())];
        for (var set = 0; set < _limitsOf.Length; set++)
        {
            foreach (var limit in _limitsOf[set])
            {
                _setsHolding[limit].Add(set);
            }
        }

        _held = [.. _limitsOf.Select(_ => new SortedSet<int>())];
        _isWoken = new bool[_limitsOf.Length];
    }

    /// <summary>
    /// A full limit has turned away a ready task: it is held until a task gives back a slot of a limit it holds. Its set sleeps,
    /// since one of its limits is full.
    /// </summary>
    public void Hold(BuiltTask task)
    {
        var set = _setOf[task.Position];
        _held[set].Add(task.Position);
        _isWoken[set] = false;
    }

    /// <summary>A task has given back a slot of a limit: the tasks held by a set of limits that includes it may fit again.</summary>
    /// <param name="limit">The limit, as its position among <see cref="Graph.LimitNames"/>.</param>
    public void Release(int limit)
    {
        foreach (var set in _setsHolding[limit])
        {
            if (!_isWoken[set] && _held[set].Count > 0)
            {
                _isWoken[set] = true;
                _woken.Add(set);
            }
        }
    }

    /// <summary>Begins a walk: from now on <see cref="First"/> offers the first task of each woken set.</summary>
    public void BeginWalk()
    {
        _offered.Clear();
        foreach (var set in _woken)
        {
            Offer(set);
        }
    }

    /// <summary>
    /// The position of the first held task, among the woken sets, whose limits all have room, or <see langword="null"/> when
    /// there is none. A woken set with a full limit sleeps here, without its tasks being tried.
    /// </summary>
    public int? First()
    {
        while (_offered.TryPeek(out var set, out var position))
        {
            if (_isWoken[set] && !AnyIsFull(_limitsOf[set]))
            {
                return position;
            }

            _offered.Dequeue();
            _isWoken[set] = false;
        }

        return null;
    }

    /// <summary>The task <see cref="First"/> gave has started: it is no longer held, and the task behind it in its set is offered.</summary>
    public void StartedFirst()
    {
        var set = _offered.Dequeue();
        _held[set].Remove(_held[set].Min);
        Offer(set);
    }

    /// <summary>Ends a walk: a set stays woken only while it holds a task, so that the next walk offers it.</summary>
    public void EndWalk()
    {
        foreach (var set in _woken)
        {
            if (_held[set].Count == 0)
            {
                _isWoken[set] = false;
            }
        }

        _woken.RemoveAll(set => !_isWoken[set]);
    }

    private void Offer(int set)
    {
        if (_isWoken[set] && _held[set].Count > 0)
        {
            _offered.Enqueue(set, _held[set].Min);
        }
    }

    private bool AnyIsFull(int[] limits)
    {
        foreach (var limit in limits)
        {
            if (_slots.IsFull(limit))
            {
                return true;
            }
        }

        return false;
    }
}
