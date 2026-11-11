// PROTOTYPE. Variant C: what has happened scrolls away above as permanent lines the terminal keeps,
// and only a short region at the bottom is live. Nothing is lost when more happens than fits.

namespace Dagline.Prototypes.TerminalUi;

internal sealed class VariantFeed
{
    public const string Name = "Feed with a pinned footer";

    private const int WarningsListedPerTask = 3;

    private readonly Dictionary<Node, int> _warningsListed = [];
    private int _cursor;

    public void Reset(Sim sim, bool fromStart) => _cursor = fromStart ? 0 : Math.Max(0, sim.Events.Count - 12);

    /// <summary>The permanent lines for everything that happened since the last frame.</summary>
    public List<Line> Drain(Sim sim, int width)
    {
        var lines = new List<Line>();
        for (; _cursor < sim.Events.Count; _cursor++)
        {
            var e = sim.Events[_cursor];
            var time = new Line().Add(Fmt.Clock(e.Time) + " ", S.Dim);
            switch (e.Kind)
            {
                case EventKind.RunStarted:
                    lines.Add(new Line().Add(time).Add($"Run {sim.RunNumber} of journal {sim.JournalId}", S.Bold)
                        .Add($"  {sim.Description} {Glyphs.Dot} {Fmt.Num(sim.Root.TaskCount)} tasks {Glyphs.Dot} cap {sim.Cap}", S.Dim).Fit(width));
                    if (sim.Root.EarlierCount > 0)
                    {
                        lines.Add(new Line().Add(time).Add(Glyphs.For(Status.Succeeded) + " ", S.DimGreen)
                            .Add($"{Fmt.Num(sim.Root.EarlierCount)} tasks succeeded in an earlier run and will not run again", S.Dim).Fit(width));
                    }

                    if (sim.SetAsideCount > 0)
                    {
                        lines.Add(new Line().Add(time).Add($"  {sim.SetAsideCount} earlier success set aside: it runs again", S.Dim).Fit(width));
                    }

                    break;

                case EventKind.GroupSettled:
                    AddGroup(sim, e.Node!, time, width, lines);
                    break;

                case EventKind.TaskFailed:
                    var task = e.Node!;
                    var kind = task.Unexpected ? "unexpected failure" : "reported failure";
                    lines.Add(new Line().Add(time).Add(Glyphs.For(Status.Failed) + " ", S.BoldRed).Add(task.Path, S.BoldRed).Add($"  {kind}", S.Dim).Fit(width));
                    lines.Add(new Line().Add("           " + task.Message, S.Red).Fit(width));
                    foreach (var detail in task.Details.Take(3))
                    {
                        lines.Add(new Line().Add("           " + detail, S.Dim).Fit(width));
                    }

                    var more = task.Details.Length - 3 + task.DetailsDropped;
                    if (more > 0)
                    {
                        lines.Add(new Line().Add($"           and {Fmt.Num(more)} more lines, in the summary", S.Dim));
                    }

                    if (task.BlocksCount > 0)
                    {
                        lines.Add(new Line().Add("           ").Add($"{Glyphs.For(Status.Blocked)} {task.BlocksCount} tasks are blocked behind it", S.Maroon));
                    }

                    break;

                case EventKind.TaskCancelled:
                    lines.Add(new Line().Add(time).Add(Glyphs.For(Status.Cancelled) + " ", S.Yellow).Add(e.Node!.Path, S.Yellow)
                        .Add("  cancelled" + (e.Text.Length > 0 ? ": " + e.Text : ""), S.Dim).Fit(width));
                    break;

                case EventKind.Warning:
                    var listed = _warningsListed.GetValueOrDefault(e.Node!);
                    _warningsListed[e.Node!] = listed + 1;
                    if (listed < WarningsListedPerTask)
                    {
                        lines.Add(new Line().Add(time).Add(Glyphs.Warning + " ", S.Yellow).Add(e.Node!.Path + "  ", S.Dim).Add(e.Text).Fit(width));
                    }
                    else if (listed == WarningsListedPerTask)
                    {
                        lines.Add(new Line().Add(time).Add(Glyphs.Warning + " ", S.Yellow).Add(e.Node!.Path + "  ", S.Dim)
                            .Add("more warnings: counted from here, listed in the summary", S.Dim).Fit(width));
                    }

                    break;

                case EventKind.StopRequested:
                    lines.Add(new Line().Add(time).Add("Stop requested", S.Yellow)
                        .Add($"  waiting up to {Fmt.Dur(sim.GracePeriod)} for {e.Text} tasks", S.Dim).Fit(width));
                    break;
            }
        }

        return lines;
    }

    private static void AddGroup(Sim sim, Node group, Line time, int width, List<Line> lines)
    {
        var status = group.Derived;

        // A blocked group is announced once, at the highest group that is blocked as a whole.
        if (status == Status.Blocked && group.Parent is { } parent && parent != sim.Root && parent.Derived == Status.Blocked)
        {
            return;
        }

        var (glyph, style) = Fmt.Mark(group);
        var line = new Line().Add(time).Add(glyph + " ", style).Add(group.Path, group.Depth <= 2 ? S.Bold : S.None);
        line.Add($"  {Fmt.Num(group.TaskCount)} tasks", S.Dim);
        if (group.StartedAt is { } started)
        {
            line.Add($" {Glyphs.Dot} {Fmt.Dur((group.SettledAt ?? sim.Now) - started)}", S.Dim);
        }

        if (status == Status.Blocked)
        {
            line.Add($"  blocked by {group.FirstFailure?.Path}", S.Maroon);
        }
        else if (status != Status.Succeeded)
        {
            line.Add("  ").Add(Fmt.Tally(group, words: false));
        }
        else if (group.WarningCount > 0)
        {
            line.Add($"  {Glyphs.Warning} {Fmt.Num(group.WarningCount)}", S.Yellow);
        }

        lines.Add(line.Fit(width));
    }

    public static List<Line> BuildFooter(Sim sim, int width, int height)
    {
        var lines = new List<Line> { new Line().Add(Fmt.Repeat(Glyphs.Rule, width), S.Dim) };
        var root = sim.Root;
        var working = sim.Running.Where(t => !t.IsWaiting).OrderBy(t => sim.Tasks.IndexOf(t)).ToList();
        var room = Math.Clamp(height / 4, 3, 10);
        var pathWidth = Math.Clamp(width / 2, 16, 44);
        var shown = working.Count <= room ? working.Count : room - 1;
        foreach (var task in working.Take(shown))
        {
            var left = new Line().Add(Glyphs.For(Status.Running) + " ", S.Blue).Add(Fmt.TailPath(task.Path, pathWidth).PadRight(pathWidth) + " ")
                .Add(Fmt.Progress(task, width >= 100 ? 16 : 8));
            lines.Add(Line.Split(left, new Line().Add(Fmt.Since(sim, task), S.Dim), width));
        }

        if (shown < working.Count)
        {
            lines.Add(new Line().Add($"  and {working.Count - shown} more working", S.Dim));
        }

        if (root.WaitingCount > 0)
        {
            var longest = sim.Running.Where(t => t.IsWaiting).MinBy(t => t.WaitingSince)!;
            lines.Add(new Line().Add($"{Glyphs.Waiting} {root.WaitingCount} waiting on external systems", S.Cyan)
                .Add($" {Glyphs.Dot} longest {Fmt.Dur(sim.Now - longest.WaitingSince!.Value)}: {longest.Path} ({longest.ProgressText})", S.Dim).Fit(width));
        }

        if (Fmt.StopBanner(sim, width) is { } banner)
        {
            lines.Add(banner);
        }

        var settled = root.TaskCount - root.Count(Status.Pending) - root.Count(Status.Ready) - root.Count(Status.Running);
        var right = new Line().Add($" {Fmt.Num(settled)} of {Fmt.Num(root.TaskCount)} settled", S.None).Add("  elapsed ", S.Dim).Add(Fmt.Dur(sim.Elapsed), S.Bold);
        lines.Add(Fmt.StackedBar(root, Math.Max(10, width - right.Length)).Add(right).Fit(width));
        lines.Add(Fmt.Tally(root, words: width >= 110).Fit(width));
        return lines;
    }
}
