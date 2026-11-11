// PROTOTYPE. Variant B: no tree. Fixed panes that each answer one question:
// where is the run (a strip of cells per group), what is working, what is waiting, what went wrong.

namespace Dagline.Prototypes.TerminalUi;

internal static class VariantDashboard
{
    public const string Name = "Dashboard of panes";

    public static List<Line> Build(Sim sim, int width, int height)
    {
        var lines = new List<Line>();
        var root = sim.Root;

        var settled = root.TaskCount - root.Count(Status.Pending) - root.Count(Status.Ready) - root.Count(Status.Running);
        var percent = root.TaskCount == 0 ? 0 : settled * 100 / root.TaskCount;
        var title = new Line().Add("Dagline", S.Bold).Add($"  {sim.Description}").Add($"  journal {sim.JournalId} {Glyphs.Dot} run {sim.RunNumber}", S.Dim);
        var clock = new Line().Add($"{percent}%", S.Bold).Add("  elapsed ", S.Dim).Add(Fmt.Dur(sim.Elapsed), S.Bold);
        lines.Add(Line.Split(title, clock, width));
        lines.Add(Fmt.StackedBar(root, width));
        var limits = Limits(sim);
        var tally = Fmt.Tally(root, words: true);
        lines.Add(Line.Split(tally.Length + limits.Length + 2 <= width ? tally : Fmt.Tally(root, words: false), limits, width));
        if (Fmt.StopBanner(sim, width) is { } banner)
        {
            lines.Add(banner);
        }

        var working = sim.Running.Where(t => !t.IsWaiting).OrderBy(t => sim.Tasks.IndexOf(t)).ToList();
        var waiting = sim.Running.Where(t => t.IsWaiting).OrderBy(t => t.WaitingSince).ToList();
        var failed = sim.Tasks.Where(t => t.Status is Status.Failed or Status.Cancelled).OrderByDescending(t => t.SettledAt).ToList();
        var warnings = sim.Events.Where(e => e.Kind == EventKind.Warning).TakeLast(3).Reverse().ToList();

        // The strip pane takes what it needs. The other three share the rest: problems first, then waiting, then working.
        var strips = Strips(sim, width, Math.Max(3, (height - lines.Count) / 2));
        var rest = height - lines.Count - strips.Count;
        var problemsWant = failed.Count == 0 && warnings.Count == 0 ? 0 : 1 + (failed.Count * 2) + warnings.Count;
        var problemsRoom = Math.Min(problemsWant, Math.Max(3, rest / 3));
        var waitingWant = waiting.Count == 0 ? 0 : 1 + Math.Min(waiting.Count, 5);
        var waitingRoom = Math.Min(waitingWant, Math.Max(2, (rest - problemsRoom) / 3));
        var workingRoom = Math.Max(2, rest - problemsRoom - waitingRoom);

        lines.AddRange(strips);
        lines.AddRange(WorkingPane(sim, working, width, workingRoom));
        if (waitingRoom > 0)
        {
            lines.AddRange(WaitingPane(sim, waiting, width, waitingRoom));
        }

        if (problemsRoom > 0)
        {
            lines.AddRange(ProblemsPane(sim, failed, warnings, width, problemsRoom));
        }

        return lines.Count > height ? lines[..height] : lines;
    }

    private static Line Limits(Sim sim)
    {
        var line = new Line().Add($"cap {sim.Running.Count}/{sim.Cap}", S.Dim);
        var full = sim.LimitSizes.Count(l => sim.LimitUsed.GetValueOrDefault(l.Key) >= l.Value);
        if (full > 0)
        {
            line.Add($" {Glyphs.Dot} {full} of {sim.LimitSizes.Count} limits full", S.Dim);
        }

        var ready = sim.Root.Count(Status.Ready);
        if (ready > 0)
        {
            line.Add($" {Glyphs.Dot} {Fmt.Num(ready)} ready, held", S.None);
        }

        return line;
    }

    /// <summary>One row per group two levels down, one cell per node beneath it. Falls back to one level when rows run out.</summary>
    private static List<Line> Strips(Sim sim, int width, int room)
    {
        var rows = sim.Root.Children.SelectMany(top =>
            top.IsGroup && top.Children.Exists(c => c.IsGroup) ? top.Children.Where(c => c.IsGroup) : [top]).ToList();
        var columns = width >= 110 ? 2 : 1;
        if ((rows.Count + columns - 1) / columns > room - 1)
        {
            rows = sim.Root.Children;
            columns = 1;
        }

        var perColumn = (rows.Count + columns - 1) / columns;
        var columnWidth = (width - ((columns - 1) * 3)) / columns;
        var labelWidth = Math.Min(rows.Max(r => r.Path.Length), Math.Max(8, columnWidth / 3));
        var lines = new List<Line> { Fmt.Rule("Groups", width) };
        for (var r = 0; r < perColumn && lines.Count < room; r++)
        {
            var line = new Line();
            for (var c = 0; c < columns; c++)
            {
                var index = (c * perColumn) + r;
                var cell = index < rows.Count ? Strip(rows[index], labelWidth, columnWidth) : new Line();
                line.Add(cell.PadTo(columnWidth));
                if (c < columns - 1)
                {
                    line.Add("   ");
                }
            }

            lines.Add(line.Fit(width));
        }

        return lines;
    }

    private static Line Strip(Node group, int labelWidth, int width)
    {
        var (glyph, style) = Fmt.Mark(group);
        var quiet = group.Derived is Status.Pending or Status.Succeeded;
        var line = new Line().Add(glyph + " ", style).Add(Fmt.TailPath(group.Path, labelWidth).PadRight(labelWidth) + " ", quiet ? S.Dim : S.None);

        var tail = new Line().Add($" {group.Count(Status.Succeeded)}/{group.TaskCount}", S.Dim);
        if (group.Count(Status.Failed) > 0)
        {
            tail.Add($" {Glyphs.For(Status.Failed)}{group.Count(Status.Failed)}", S.BoldRed);
        }

        if (group.WarningCount > 0)
        {
            tail.Add($" {Glyphs.Warning}{Fmt.Num(group.WarningCount)}", S.Yellow);
        }

        var cellRoom = width - line.Length - tail.Length;
        var cells = group.Children;
        if (cells.Count == 0 || cells.Count > cellRoom)
        {
            line.Add(Fmt.StackedBar(group, Math.Clamp(cellRoom, 0, 20)));
        }
        else
        {
            foreach (var cell in cells)
            {
                var (g, s) = Fmt.Mark(cell);
                line.Add(g, s);
            }
        }

        return line.Add(tail).Fit(width);
    }

    private static List<Line> WorkingPane(Sim sim, List<Node> working, int width, int room)
    {
        var lines = new List<Line> { Fmt.Rule($"Working ({working.Count})", width) };
        var pathWidth = Math.Clamp(width / 2, 16, 44);
        var shown = working.Count <= room - 1 ? working.Count : room - 2;
        foreach (var task in working.Take(Math.Max(0, shown)))
        {
            var left = new Line().Add(Glyphs.For(Status.Running) + " ", S.Blue).Add(Fmt.TailPath(task.Path, pathWidth).PadRight(pathWidth) + " ");
            left.Add(Fmt.Progress(task, width >= 100 ? 16 : 8));
            var right = new Line();
            if (task.Warnings.Count > 0)
            {
                right.Add($"{Glyphs.Warning} {Fmt.Num(task.Warnings.Count + task.WarningsDropped)} ", S.Yellow);
            }

            right.Add(Fmt.Since(sim, task), S.Dim);
            lines.Add(Line.Split(left, right, width));
        }

        if (shown < working.Count)
        {
            lines.Add(new Line().Add($"  and {working.Count - Math.Max(0, shown)} more working", S.Dim));
        }
        else if (working.Count == 0)
        {
            lines.Add(new Line().Add(sim.Root.WaitingCount > 0 ? "  nothing is working: every running task is waiting" : "  nothing is working", S.Dim));
        }

        return lines;
    }

    private static List<Line> WaitingPane(Sim sim, List<Node> waiting, int width, int room)
    {
        var longest = waiting.Count == 0 ? TimeSpan.Zero : sim.Now - waiting[0].WaitingSince!.Value;
        var lines = new List<Line>
        {
            Fmt.Rule($"Waiting on external systems ({waiting.Count})", width, new Line().Add($"longest {Fmt.Dur(longest)}", S.Dim)),
        };
        var pathWidth = Math.Clamp(width / 2, 16, 44);
        var shown = waiting.Count <= room - 1 ? waiting.Count : room - 2;
        foreach (var task in waiting.Take(Math.Max(0, shown)))
        {
            var left = new Line().Add(Glyphs.Waiting + " ", S.Cyan).Add(Fmt.TailPath(task.Path, pathWidth).PadRight(pathWidth) + " ")
                .Add(task.ProgressText ?? "", S.Cyan);
            lines.Add(Line.Split(left, new Line().Add(Fmt.Dur(sim.Now - task.WaitingSince!.Value), S.Dim), width));
        }

        if (shown < waiting.Count && room > 1)
        {
            var newest = sim.Now - waiting[^1].WaitingSince!.Value;
            lines.Add(new Line().Add($"  and {waiting.Count - Math.Max(0, shown)} more, the newest waiting {Fmt.Dur(newest)}", S.Dim));
        }

        return lines.Count > room ? lines[..room] : lines;
    }

    private static List<Line> ProblemsPane(Sim sim, List<Node> failed, List<SimEvent> warnings, int width, int room)
    {
        var root = sim.Root;
        var summary = new Line();
        if (root.Count(Status.Blocked) > 0)
        {
            summary.Add($"{Fmt.Num(root.Count(Status.Blocked))} tasks blocked behind {root.Count(Status.Failed)} failures", S.Maroon);
        }

        var stopped = root.Count(Status.Cancelled) > 0 ? $"{root.Count(Status.Cancelled)} cancelled, " : "";
        var lines = new List<Line>
        {
            Fmt.Rule($"Problems ({root.Count(Status.Failed)} failed, {stopped}{Fmt.Num(root.WarningCount)} warnings)", width, summary),
        };
        foreach (var task in failed)
        {
            if (lines.Count >= room)
            {
                break;
            }

            var cancelled = task.Status == Status.Cancelled;
            var head = new Line().Add(Glyphs.For(task.Status) + " ", cancelled ? S.Yellow : S.BoldRed).Add(task.Path, cancelled ? S.Yellow : S.Red);
            var right = new Line().Add(Fmt.Clock(task.SettledAt ?? sim.Now), S.Dim);
            if (task.BlocksCount > 0)
            {
                right = new Line().Add($"{Glyphs.For(Status.Blocked)} blocks {task.BlocksCount}  ", S.Maroon).Add(right);
            }

            lines.Add(Line.Split(head, right, width));
            if (lines.Count < room && !string.IsNullOrEmpty(task.Message))
            {
                lines.Add(new Line().Add("    " + task.Message, S.None).Fit(width));
            }
        }

        foreach (var warning in warnings)
        {
            if (lines.Count >= room)
            {
                break;
            }

            lines.Add(new Line().Add(Glyphs.Warning + " ", S.Yellow).Add(warning.Node!.Path + "  ", S.Dim).Add(warning.Text).Fit(width));
        }

        return lines;
    }
}
