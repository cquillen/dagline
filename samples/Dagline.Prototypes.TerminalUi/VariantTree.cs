// PROTOTYPE. Variant A: the graph as a tree in declaration order that opens as far as the screen allows.
// A group is one line until it is opened. Groups with something to show (running, failed, cancelled) are opened
// breadth first, failures before the rest and otherwise in declaration order, until the next one would not fit.

namespace Dagline.Prototypes.TerminalUi;

internal static class VariantTree
{
    public const string Name = "Tree that folds to fit";

    public static List<Line> Build(Sim sim, int width, int height)
    {
        var lines = new List<Line>();
        var root = sim.Root;

        var title = new Line().Add("Dagline", S.Bold).Add($"  {sim.Description}", S.None)
            .Add($"  journal {sim.JournalId} {Glyphs.Dot} run {sim.RunNumber}", S.Dim);
        var clock = new Line().Add("elapsed ", S.Dim).Add(Fmt.Dur(sim.Elapsed), S.Bold);
        lines.Add(Line.Split(title, clock, width));

        var settled = root.TaskCount - root.Count(Status.Pending) - root.Count(Status.Ready) - root.Count(Status.Running);
        var count = new Line().Add($" {Fmt.Num(settled)} of {Fmt.Num(root.TaskCount)} settled", S.None);
        var barWidth = Math.Max(10, Math.Min(50, width - count.Length - 2));
        lines.Add(Fmt.StackedBar(root, barWidth).Add(count).Fit(width));
        lines.Add(Fmt.Tally(root, words: width >= 110).Fit(width));
        if (Fmt.StopBanner(sim, width) is { } banner)
        {
            lines.Add(banner);
        }

        lines.Add(new Line());

        var budget = Math.Max(3, height - lines.Count);
        var open = new HashSet<Node>();
        var body = Render(sim, open, width);

        void OpenBreadthFirst(Func<Node, bool> wanted)
        {
            var level = root.Children.Where(c => c.IsGroup && wanted(c)).ToList();
            while (level.Count > 0)
            {
                var next = new List<Node>();
                foreach (var group in level)
                {
                    if (open.Add(group))
                    {
                        var trial = Render(sim, open, width);
                        if (trial.Count > budget)
                        {
                            open.Remove(group);
                            return;
                        }

                        body = trial;
                    }

                    next.AddRange(group.Children.Where(c => c.IsGroup && wanted(c)));
                }

                level = next;
            }
        }

        // Failures are opened all the way down first, so their messages show. What is left goes to running groups.
        OpenBreadthFirst(HasFailure);
        OpenBreadthFirst(Interesting);

        if (body.Count > budget)
        {
            var hidden = body.Count - budget + 1;
            body = body[..(budget - 1)];
            body.Add(new Line().Add($"  {Glyphs.Ellipsis} {hidden} more lines do not fit", S.Dim));
        }

        lines.AddRange(body);
        return lines;
    }

    private static bool HasFailure(Node node) => node.Count(Status.Failed) > 0;

    private static List<Line> Render(Sim sim, HashSet<Node> open, int width)
    {
        var body = new List<Line>();
        foreach (var child in sim.Root.Children)
        {
            Emit(sim, child, open, width, body);
        }

        return body;
    }

    private static bool Interesting(Node node) =>
        node.Count(Status.Running) > 0 || node.Count(Status.Failed) > 0 || node.Count(Status.Cancelled) > 0;

    private static void Emit(Sim sim, Node node, HashSet<Node> open, int width, List<Line> output)
    {
        var indent = new string(' ', (node.Depth - 1) * 2);
        if (!node.IsGroup)
        {
            output.Add(TaskLine(sim, node, indent, width));
            return;
        }

        if (!open.Contains(node))
        {
            output.Add(GroupLine(sim, node, indent, width, inlineActivity: true));
            return;
        }

        output.Add(GroupLine(sim, node, indent, width, inlineActivity: false));

        // Top-level groups and their children are always listed. Deeper down, quiet siblings share one line.
        var listQuiet = node.Depth < 2;
        var folded = new Node { IsGroup = true };
        var foldedNodes = 0;
        foreach (var child in node.Children)
        {
            var show = child.IsGroup
                ? Interesting(child) || listQuiet
                : child.Status is Status.Running or Status.Failed or Status.Cancelled || listQuiet;
            if (show)
            {
                Emit(sim, child, open, width, output);
                continue;
            }

            foldedNodes++;
            folded.TaskCount += child.TaskCount;
            folded.EarlierCount += child.EarlierCount;
            for (var i = 0; i < 7; i++)
            {
                folded.Counts[i] += child.IsGroup ? child.Counts[i] : (int)child.Status == i ? 1 : 0;
            }
        }

        if (foldedNodes > 0)
        {
            var what = node.Children.TrueForAll(c => !c.IsGroup) ? "" : $" in {foldedNodes} groups";
            output.Add(new Line().Add(indent + "    ").Add(Fmt.Tally(folded, words: true)).Add(what, S.Dim).Fit(width));
        }
    }

    private static Line GroupLine(Sim sim, Node node, string indent, int width, bool inlineActivity)
    {
        var (glyph, style) = Fmt.Mark(node);
        var status = node.Derived;
        var quiet = status is Status.Pending or Status.Succeeded or Status.Blocked;
        var left = new Line().Add(indent).Add(glyph + " ", style).Add(node.Segment, quiet ? S.Dim : S.Bold).Add("  ");

        var succeeded = node.Count(Status.Succeeded);
        if (status is Status.Running or Status.Ready)
        {
            left.Add(Fmt.StackedBar(node, 12)).Add($" {Fmt.Num(succeeded)}/{Fmt.Num(node.TaskCount)}  ", S.None);
        }
        else
        {
            left.Add($"{Fmt.Num(succeeded)}/{Fmt.Num(node.TaskCount)}  ", S.Dim);
        }

        if (status == Status.Blocked && node.FirstFailure is { } cause)
        {
            left.Add($"blocked by {cause.Path}", S.Maroon);
        }
        else if (inlineActivity && node.Count(Status.Running) > 0)
        {
            left.Add(Activity(sim, node));
        }
        else if (inlineActivity && node.Count(Status.Failed) == 1 && node.FirstFailure is { Status: Status.Failed } only)
        {
            left.Add($"{only.Path[(node.Path.Length + 1)..]}: {only.Message}", S.Red);
        }

        var right = new Line();
        if (node.Count(Status.Failed) > 0)
        {
            right.Add($"{Glyphs.For(Status.Failed)} {node.Count(Status.Failed)} ", S.BoldRed);
        }

        if (node.Count(Status.Blocked) > 0 && status != Status.Blocked)
        {
            right.Add($"{Glyphs.For(Status.Blocked)} {node.Count(Status.Blocked)} ", S.Maroon);
        }

        if (node.WarningCount > 0)
        {
            right.Add($"{Glyphs.Warning} {Fmt.Num(node.WarningCount)} ", S.Yellow);
        }

        if (node.StartedAt is { } started)
        {
            right.Add(Fmt.Dur((node.IsSettled ? node.SettledAt ?? sim.Now : sim.Now) - started), S.Dim);
        }

        return Line.Split(left, right, width);
    }

    /// <summary>What a closed group is doing, said on its own line: its one busy task, or how many.</summary>
    private static Line Activity(Sim sim, Node group)
    {
        var line = new Line();
        var running = sim.Running.Where(t => t.Path.StartsWith(group.Path + "/", StringComparison.Ordinal)).ToList();
        var working = running.Where(t => !t.IsWaiting).ToList();
        var waiting = running.Where(t => t.IsWaiting).ToList();

        if (working.Count == 1)
        {
            var t = working[0];
            line.Add(t.Path[(group.Path.Length + 1)..] + " ", S.Blue).Add(Fmt.Progress(t, 8));
        }
        else if (working.Count > 1)
        {
            line.Add($"{Glyphs.For(Status.Running)} {working.Count} working", S.Blue);
            var named = working.Take(2).Select(t => t.Total is > 0 ? $"{t.Segment} {t.Current * 100 / t.Total}%" : t.Segment);
            line.Add(" " + string.Join(", ", named) + (working.Count > 2 ? $", +{working.Count - 2}" : ""), S.Dim);
        }

        if (waiting.Count > 0)
        {
            if (line.Length > 0)
            {
                line.Add("  ");
            }

            var longest = waiting.MinBy(t => t.WaitingSince)!;
            if (waiting.Count == 1)
            {
                line.Add($"{longest.Path[(group.Path.Length + 1)..]} {longest.ProgressText}", S.Cyan)
                    .Add($" {Glyphs.Dot} {Fmt.Since(sim, longest)}", S.Dim);
            }
            else
            {
                line.Add($"{Glyphs.Waiting} {waiting.Count} waiting", S.Cyan)
                    .Add($" {Glyphs.Dot} longest {Fmt.Dur(sim.Now - longest.WaitingSince!.Value)}", S.Dim);
            }
        }

        return line;
    }

    private static Line TaskLine(Sim sim, Node task, string indent, int width)
    {
        var (glyph, style) = Fmt.Mark(task);
        var left = new Line().Add(indent).Add(glyph + " ", style).Add(task.Segment, task.Status == Status.Running ? S.None : style).Add("  ");
        var right = new Line();
        if (task.Warnings.Count > 0)
        {
            right.Add($"{Glyphs.Warning} {Fmt.Num(task.Warnings.Count + task.WarningsDropped)} ", S.Yellow);
        }

        switch (task.Status)
        {
            case Status.Running:
                left.Add(Fmt.Progress(task, 12));
                right.Add(Fmt.Since(sim, task), S.Dim);
                break;
            case Status.Failed:
                left.Add(task.Message ?? "", S.Red);
                if (task.BlocksCount > 0)
                {
                    right.Add($"{Glyphs.For(Status.Blocked)} blocks {task.BlocksCount}", S.Maroon);
                }

                break;
            case Status.Blocked:
                left.Add($"blocked by {task.BlockedBy?.Path}", S.Maroon);
                break;
            case Status.Succeeded when task.Earlier:
                left.Add("earlier run", S.Dim);
                break;
            default:
                left.Add(task.Message ?? "", S.Dim);
                break;
        }

        return Line.Split(left, right, width);
    }
}
