// PROTOTYPE. A faked run of the reference scenario, driven by a simulated clock.
// Nothing here is the engine: it only produces statuses, progress and events for the layouts to draw.

namespace Dagline.Prototypes.TerminalUi;

internal enum Status { Pending, Ready, Running, Succeeded, Failed, Blocked, Cancelled }

internal enum EventKind { RunStarted, GroupSettled, TaskFailed, TaskCancelled, Warning, StopRequested }

internal sealed record SimEvent(DateTimeOffset Time, EventKind Kind, Node? Node, string Text);

internal sealed class Node
{
    public string Segment = "";
    public string Path = "";
    public Node? Parent;
    public int Depth;
    public bool IsGroup;
    public List<Node> Children = [];
    public List<Node> Requires = [];
    public List<Node> After = [];
    public List<string> Limits = [];

    // Roll-ups over the tasks beneath a group (for a task: itself).
    public int[] Counts = new int[7];
    public int TaskCount;
    public int WaitingCount;
    public int EarlierCount;
    public int WarningCount;
    public Node? FirstFailure;
    public DateTimeOffset? StartedAt;
    public DateTimeOffset? SettledAt;
    public bool Announced;

    // Task state.
    public Status Status;
    public bool Earlier;
    public bool SetAside;
    public double Elapsed;
    public DateTimeOffset? WaitingSince;
    public string? ProgressText;
    public long? Current;
    public long? Total;
    public List<string> Warnings = [];
    public int WarningsDropped;
    public string? Message;
    public string[] Details = [];
    public int DetailsDropped;
    public bool Unexpected;
    public Node? BlockedBy;
    public int BlocksCount;

    // The script the fake follows.
    public double WorkSeconds;
    public double WaitSeconds;
    public long PlanTotal;
    public string WorkText = "";
    public string? JobId;
    public int PlanWarnings;
    public string? PlanFailMessage;
    public string[] PlanFailDetails = [];
    public int PlanFailDetailsDropped;
    public bool PlanFailUnexpected;
    public bool PlanFailWhileWaiting;
    public bool IgnoresStop;
    public double StopLag;

    public bool IsWaiting => Status == Status.Running && WaitingSince is not null;

    public bool IsSettled => IsGroup
        ? Counts[(int)Status.Pending] + Counts[(int)Status.Ready] + Counts[(int)Status.Running] == 0
        : Status >= Status.Succeeded;

    /// <summary>A group's status is derived from the tasks beneath it, as the lifecycle ticket decided.</summary>
    public Status Derived
    {
        get
        {
            if (!IsGroup)
            {
                return Status;
            }

            if (Counts[(int)Status.Running] > 0) return Status.Running;
            if (Counts[(int)Status.Ready] > 0) return Status.Ready;
            if (Counts[(int)Status.Pending] > 0) return Status.Pending;
            if (Counts[(int)Status.Failed] > 0) return Status.Failed;
            if (Counts[(int)Status.Cancelled] > 0) return Status.Cancelled;
            if (Counts[(int)Status.Blocked] > 0) return Status.Blocked;
            return Status.Succeeded;
        }
    }

    public int Count(Status s) => Counts[(int)s];

    public int Working => Count(Status.Running) - WaitingCount;
}

internal sealed class Sim
{
    public const int WarningLimit = 100;

    public readonly Node Root = new() { IsGroup = true };
    public readonly List<Node> Tasks = [];
    public readonly List<Node> Running = [];
    public readonly List<SimEvent> Events = [];
    public readonly Dictionary<string, int> LimitSizes = new() { ["scheduler"] = 60 };
    public readonly Dictionary<string, int> LimitUsed = [];
    public int Cap = 96;

    public string JournalId = "2026-11-11-0412-a3f";
    public string Description = "all shards, month-end";
    public int RunNumber = 1;
    public int SetAsideCount;

    public DateTimeOffset StartedAt;
    public DateTimeOffset Now;
    public DateTimeOffset? StopRequestedAt;
    public DateTimeOffset? StopDeadline;
    public bool Ended;
    public TimeSpan GracePeriod = TimeSpan.FromSeconds(30);

#pragma warning disable IDE0044 // reassigned in the constructor
    private Random _rng;
#pragma warning restore IDE0044
    private bool _dirty = true;
    private bool _failDirty;

    public TimeSpan Elapsed => Now - StartedAt;

    public string Outcome =>
        Root.Count(Status.Succeeded) == Root.TaskCount ? "complete" : StopRequestedAt is not null ? "stopped" : "incomplete";

    public Sim(int seed, Sim? previous = null)
    {
        // The graph is the same in every run; only what goes wrong differs.
        _rng = new Random(7);
        StartedAt = Now = DateTimeOffset.Now;
        BuildGraph();
        _rng = new Random(seed);
        PlanTrouble(withFailures: previous is null);
        if (previous is not null)
        {
            ContinueFrom(previous);
        }

        Events.Add(new SimEvent(Now, EventKind.RunStarted, null, ""));
    }

    // ---------------------------------------------------------------- graph

    private Node Add(Node parent, string segment, bool group)
    {
        var node = new Node
        {
            Segment = segment,
            Parent = parent,
            Depth = parent.Depth + 1,
            IsGroup = group,
            Path = parent.Path.Length == 0 ? segment : parent.Path + "/" + segment,
        };
        parent.Children.Add(node);
        if (!group)
        {
            Tasks.Add(node);
        }

        return node;
    }

    private Node Work(Node parent, string segment, double seconds, long total, string text, params Node[] requires)
    {
        var task = Add(parent, segment, group: false);
        task.WorkSeconds = seconds;
        task.PlanTotal = total;
        task.WorkText = text;
        task.Requires.AddRange(requires);
        return task;
    }

    private void BuildGraph()
    {
        var prepare = Add(Root, "prepare", group: true);
        var settings = Work(prepare, "load-settings", 8, 0, "reading settings");
        Work(prepare, "discover-tenants", 25, 10, "listing shards", settings);

        var validate = Add(Root, "validate", group: true);
        validate.Requires.Add(prepare);
        var process = Add(Root, "process", group: true);

        var tenantNumber = 0;
        var jobNumber = 48_000;
        for (var s = 1; s <= 10; s++)
        {
            var shardName = $"shard-{s:00}";
            var limit = $"db-{shardName}";
            LimitSizes[limit] = 2;

            var vShard = Add(validate, shardName, group: true);
            vShard.Limits.Add(limit);
            var pShard = Add(process, shardName, group: true);
            pShard.Requires.Add(vShard);
            var pTenants = new List<Node>();

            var tenants = _rng.Next(7, 12);
            for (var t = 0; t < tenants; t++)
            {
                var tenantName = $"tenant-{++tenantNumber:000}";
                var entities = _rng.Next(2_000, 9_500);
                double Secs() => 35 + (_rng.NextDouble() * 80);

                var v = Add(vShard, tenantName, group: true);
                var schema = Work(v, "schema", Secs(), entities, "checking entities");
                var references = Work(v, "references", Secs(), entities, "resolving references", schema);
                var totals = Work(v, "totals", Secs(), entities, "summing entities", schema);
                var duplicates = Work(v, "duplicates", Secs(), entities, "comparing entities", schema);
                var ranges = Work(v, "ranges", Secs(), entities, "checking ranges", schema);
                var encoding = Work(v, "encoding", Secs(), entities, "scanning text", schema);
                var dates = Work(v, "dates", Secs(), entities, "checking dates", schema);
                var identifiers = Work(v, "identifiers", Secs(), entities, "checking identifiers", schema);
                var ownership = Work(v, "ownership", Secs(), entities, "matching owners", references);
                var history = Work(v, "history", Secs(), entities, "replaying history", references);
                var cross = Work(v, "cross-check", Secs(), entities, "cross-checking", totals, duplicates);
                Work(v, "sign-off", 6, 0, "recording sign-off", ranges, encoding, dates, identifiers, ownership, history, cross);

                var p = Add(pShard, tenantName, group: true);
                pTenants.Add(p);
                Node? previousJob = null;
                foreach (var step in (string[])["extract", "transform", "load", "reconcile", "publish", "archive"])
                {
                    var job = Add(p, step, group: false);
                    job.Limits.Add("scheduler");
                    job.WorkSeconds = 4 + (_rng.NextDouble() * 10);
                    job.WorkText = "triggering external job";
                    job.WaitSeconds = (8 + (_rng.NextDouble() * 27)) * 60;
                    job.JobId = (++jobNumber).ToString();
                    if (previousJob is not null)
                    {
                        job.Requires.Add(previousJob);
                    }

                    previousJob = job;
                }
            }

            Work(pShard, "close-shard", 20, 0, "closing shard", [.. pTenants]);
        }

        var finish = Add(Root, "finish", group: true);
        var totalsReport = Work(finish, "totals-report", 45, 10, "writing totals", process);
        var runReport = Add(finish, "run-report", group: false);
        runReport.WorkSeconds = 30;
        runReport.WorkText = "writing run report";
        runReport.After.Add(process);
        var notify = Add(finish, "notify", group: false);
        notify.WorkSeconds = 5;
        notify.WorkText = "sending notice";
        notify.After.Add(runReport);
        _ = totalsReport;

        foreach (var task in Tasks)
        {
            for (var n = task.Parent; n is not null; n = n.Parent)
            {
                task.Limits.AddRange(n.Limits);
                n.TaskCount++;
                n.Counts[(int)Status.Pending]++;
            }

            task.TaskCount = 1;
        }
    }

    private Node Find(string path) => Tasks.First(t => t.Path == path);

    private Node Nth(string groupPath, int tenantIndex, string segment)
    {
        var group = Root;
        foreach (var part in groupPath.Split('/'))
        {
            group = group.Children.First(c => c.Segment == part);
        }

        return group.Children[tenantIndex].Children.First(c => c.Segment == segment);
    }

    private void PlanTrouble(bool withFailures)
    {
        foreach (var task in Tasks.Where(t => t.Path.StartsWith("validate/", StringComparison.Ordinal)))
        {
            if (_rng.NextDouble() < 0.06)
            {
                task.PlanWarnings = _rng.Next(1, 5);
            }
        }

        Nth("validate/shard-02", 1, "references").PlanWarnings = 3_014;

        if (!withFailures)
        {
            var late = Nth("process/shard-03", 2, "reconcile");
            late.PlanFailMessage = $"external job {late.JobId} ended in error: exit code 3";
            late.PlanFailDetails = ["scheduler said: step 2 of 5 exceeded its memory allowance"];
            late.PlanFailWhileWaiting = true;
            return;
        }

        var totals = Nth("validate/shard-03", 1, "totals");
        totals.PlanFailMessage = $"412 of {totals.PlanTotal:N0} entities failed the totals check";
        totals.PlanFailDetails =
        [
            "entity 10482: total 118.20, expected 181.20",
            "entity 10511: total 0.00, expected 42.75",
            "entity 10513: total 96.10, expected 96.01",
            "entity 10790: total missing",
            "entity 11002: total 5,210.00, expected 521.00",
        ];
        totals.PlanFailDetailsDropped = 407;

        var history = Nth("validate/shard-07", 4, "history");
        history.PlanFailUnexpected = true;
        history.PlanFailMessage = "InvalidOperationException: Sequence contains no elements";
        history.PlanFailDetails =
        [
            "at System.Linq.ThrowHelper.ThrowNoElementsException()",
            "at Sample.Validation.HistoryCheck.LatestRevision(Entity entity) in HistoryCheck.cs:line 88",
            "at Sample.Validation.HistoryCheck.RunAsync(TaskContext context) in HistoryCheck.cs:line 41",
        ];

        var transform = Nth("process/shard-05", 2, "transform");
        transform.PlanFailMessage = $"external job {transform.JobId} ended in error: exit code 12";
        transform.PlanFailDetails = ["scheduler said: step 3 of 7 failed", "scheduler said: see job log 7f31c2"];
        transform.PlanFailWhileWaiting = true;

        var load = Nth("process/shard-09", 0, "load");
        load.PlanFailMessage = $"external job {load.JobId} ended in error: cancelled by its operator";
        load.PlanFailWhileWaiting = true;
    }

    private void ContinueFrom(Sim previous)
    {
        RunNumber = previous.RunNumber + 1;
        JournalId = previous.JournalId;
        Description = previous.Description;
        var earlier = previous.Tasks.ToDictionary(t => t.Path);

        bool WasSuccess(Node n) => n.IsGroup
            ? Tasks.Where(t => t.Path.StartsWith(n.Path + "/", StringComparison.Ordinal)).All(t => earlier[t.Path].Status == Status.Succeeded)
            : earlier[n.Path].Status == Status.Succeeded;

        foreach (var task in Tasks)
        {
            var old = earlier[task.Path];
            if (old.Status != Status.Succeeded)
            {
                continue;
            }

            // Only an "after" dependency sets an earlier success aside (ADR 0008).
            if (task.After.Any(a => !WasSuccess(a)))
            {
                task.SetAside = true;
                SetAsideCount++;
                continue;
            }

            task.Earlier = true;
            task.Message = old.Message;
            task.Warnings = old.Warnings;
            task.WarningsDropped = old.WarningsDropped;
            task.SettledAt = old.SettledAt;
            Move(task, Status.Succeeded);
            for (Node? n = task; n is not null; n = n.Parent)
            {
                n.EarlierCount++;
                n.WarningCount += task.Warnings.Count + task.WarningsDropped;
            }
        }

        foreach (var group in AllGroups(Root).Where(g => g.IsSettled))
        {
            group.Announced = true;
        }
    }

    private static IEnumerable<Node> AllGroups(Node node)
    {
        foreach (var child in node.Children.Where(c => c.IsGroup))
        {
            yield return child;
            foreach (var deeper in AllGroups(child))
            {
                yield return deeper;
            }
        }
    }

    // ---------------------------------------------------------------- stepping

    public static Sim RunToEnd(Sim sim)
    {
        while (!sim.Ended)
        {
            sim.Advance(3600);
        }

        return sim;
    }

    public void Advance(double seconds)
    {
        while (seconds > 0 && !Ended)
        {
            var dt = Math.Min(seconds, 5);
            seconds -= dt;
            Step(dt);
        }
    }

    public void RequestStop()
    {
        if (Ended)
        {
            return;
        }

        if (StopRequestedAt is not null)
        {
            StopDeadline = Now;
            return;
        }

        StopRequestedAt = Now;
        StopDeadline = Now + GracePeriod;
        var stubborn = 0;
        foreach (var task in Running)
        {
            task.StopLag = 0.5 + (_rng.NextDouble() * 6);
            if (stubborn < 2 && _rng.NextDouble() < 0.2)
            {
                task.IgnoresStop = true;
                stubborn++;
            }
        }

        Events.Add(new SimEvent(Now, EventKind.StopRequested, null, $"{Running.Count} running"));
    }

    private void Step(double dt)
    {
        Now += TimeSpan.FromSeconds(dt);

        foreach (var task in Running.ToArray())
        {
            task.Elapsed += dt;
            if (StopRequestedAt is { } stopAt)
            {
                var sinceStop = (Now - stopAt).TotalSeconds;
                if (Now >= StopDeadline)
                {
                    Settle(task, Status.Cancelled, "still running when the run stopped waiting");
                }
                else if (!task.IgnoresStop && sinceStop >= task.StopLag)
                {
                    Settle(task, Status.Cancelled, null);
                }

                continue;
            }

            Play(task);
        }

        if (_failDirty)
        {
            CascadeBlocked();
        }

        if (StopRequestedAt is null)
        {
            if (_dirty)
            {
                PromoteReady();
            }

            StartReady();
        }

        if (Running.Count == 0)
        {
            Ended = true;
        }
    }

    private void Play(Node task)
    {
        var workShare = Math.Min(1, task.Elapsed / task.WorkSeconds);
        var due = (int)(task.PlanWarnings * workShare);
        while (task.Warnings.Count + task.WarningsDropped < due)
        {
            Warn(task, $"entity {10_000 + _rng.Next(80_000)} has no owner on record; default applied");
        }

        if (task.Elapsed < task.WorkSeconds)
        {
            task.ProgressText = task.WorkText;
            if (task.PlanTotal > 0)
            {
                task.Total = task.PlanTotal;
                task.Current = (long)(task.PlanTotal * workShare);
            }

            if (task.PlanFailMessage is not null && !task.PlanFailWhileWaiting && workShare > 0.5)
            {
                Fail(task);
            }

            return;
        }

        if (task.WaitSeconds > 0 && task.Elapsed < task.WorkSeconds + task.WaitSeconds)
        {
            if (task.WaitingSince is null)
            {
                task.WaitingSince = Now;
                for (var n = task.Parent; n is not null; n = n.Parent)
                {
                    n.WaitingCount++;
                }
            }

            var waited = task.Elapsed - task.WorkSeconds;
            task.ProgressText = waited < 90 ? $"job {task.JobId} queued" : $"job {task.JobId} running";
            if (task.PlanFailMessage is not null && waited > task.WaitSeconds * 0.6)
            {
                Fail(task);
            }

            return;
        }

        if (task.PlanTotal > 0)
        {
            task.Current = task.PlanTotal;
        }

        Settle(task, Status.Succeeded, task.JobId is null ? null : $"job {task.JobId} ended normally");
    }

    private void Fail(Node task)
    {
        task.Unexpected = task.PlanFailUnexpected;
        task.Details = task.PlanFailDetails;
        task.DetailsDropped = task.PlanFailDetailsDropped;
        Settle(task, Status.Failed, task.PlanFailMessage);
    }

    private void Warn(Node task, string text)
    {
        if (task.Warnings.Count < WarningLimit)
        {
            task.Warnings.Add(text);
        }
        else
        {
            task.WarningsDropped++;
        }

        for (Node? n = task; n is not null; n = n.Parent)
        {
            n.WarningCount++;
        }

        Events.Add(new SimEvent(Now, EventKind.Warning, task, text));
    }

    private void Move(Node task, Status to)
    {
        var from = task.Status;
        task.Status = to;
        for (Node? n = task; n is not null; n = n.Parent)
        {
            n.Counts[(int)from]--;
            n.Counts[(int)to]++;
        }

        // A task counts itself so that Count() works on tasks too.
        task.Counts[(int)from] = 0;
        task.Counts[(int)to] = 1;
        _dirty = true;
    }

    private void Start(Node task)
    {
        Move(task, Status.Running);
        Running.Add(task);
        foreach (var limit in task.Limits)
        {
            LimitUsed[limit] = LimitUsed.GetValueOrDefault(limit) + 1;
        }

        for (Node? n = task; n is not null; n = n.Parent)
        {
            n.StartedAt ??= Now;
        }
    }

    private void Settle(Node task, Status status, string? message)
    {
        if (task.Status == Status.Running)
        {
            Running.Remove(task);
            foreach (var limit in task.Limits)
            {
                LimitUsed[limit]--;
            }

            if (task.WaitingSince is not null)
            {
                task.WaitingSince = null;
                for (var n = task.Parent; n is not null; n = n.Parent)
                {
                    n.WaitingCount--;
                }
            }
        }

        Move(task, status);
        task.Message = message;
        task.SettledAt = Now;

        if (status == Status.Failed)
        {
            _failDirty = true;
            for (Node? n = task; n is not null; n = n.Parent)
            {
                n.FirstFailure ??= task;
            }

            Events.Add(new SimEvent(Now, EventKind.TaskFailed, task, message ?? ""));
        }
        else if (status == Status.Cancelled)
        {
            Events.Add(new SimEvent(Now, EventKind.TaskCancelled, task, message ?? ""));
        }

        AnnounceSettledGroups(task);
    }

    private void AnnounceSettledGroups(Node task)
    {
        for (var n = task.Parent; n is not null && n != Root; n = n.Parent)
        {
            if (n.IsSettled && !n.Announced)
            {
                n.Announced = true;
                n.SettledAt = Now;
                Events.Add(new SimEvent(Now, EventKind.GroupSettled, n, ""));
            }
        }
    }

    private static IEnumerable<Node> SelfAndAncestors(Node node)
    {
        for (Node? n = node; n is not null; n = n.Parent)
        {
            yield return n;
        }
    }

    private void CascadeBlocked()
    {
        _failDirty = false;
        bool changed;
        do
        {
            changed = false;
            foreach (var task in Tasks)
            {
                if (task.Status != Status.Pending)
                {
                    continue;
                }

                var cause = SelfAndAncestors(task).SelectMany(n => n.Requires).Select(u => u.FirstFailure).FirstOrDefault(f => f is not null);
                if (cause is null)
                {
                    continue;
                }

                Move(task, Status.Blocked);
                task.BlockedBy = cause;
                task.SettledAt = Now;
                cause.BlocksCount++;
                for (Node? n = task; n is not null; n = n.Parent)
                {
                    n.FirstFailure ??= cause;
                }

                AnnounceSettledGroups(task);
                changed = true;
            }
        }
        while (changed);
    }

    private void PromoteReady()
    {
        _dirty = false;
        foreach (var task in Tasks)
        {
            if (task.Status != Status.Pending)
            {
                continue;
            }

            var met = SelfAndAncestors(task).All(n =>
                n.Requires.All(u => u.Derived == Status.Succeeded) && n.After.All(u => u.IsSettled));
            if (met)
            {
                Move(task, Status.Ready);
            }
        }

        _dirty = false;
    }

    private void StartReady()
    {
        if (Root.Count(Status.Ready) == 0)
        {
            return;
        }

        foreach (var task in Tasks)
        {
            if (Running.Count >= Cap)
            {
                return;
            }

            if (task.Status == Status.Ready && task.Limits.All(l => LimitUsed.GetValueOrDefault(l) < LimitSizes[l]))
            {
                Start(task);
            }
        }
    }
}
