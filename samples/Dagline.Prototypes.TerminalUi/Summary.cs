// PROTOTYPE. The end-of-run summary, written once to the ordinary terminal after the live display has gone.

using Spectre.Console;

namespace Dagline.Prototypes.TerminalUi;

internal static class Summary
{
    public static List<Line> Build(Sim sim, int width)
    {
        var root = sim.Root;
        var lines = new List<Line> { new Line().Add(Fmt.Repeat(Glyphs.Rule, width), S.Dim) };

        var (style, exit) = sim.Outcome switch
        {
            "complete" => (Style.Parse("bold green"), 0),
            "incomplete" => (S.BoldRed, 1),
            _ => (Style.Parse("bold yellow"), 2),
        };
        lines.Add(new Line().Add("Run " + sim.Outcome, style)
            .Add($"  {Fmt.Dur(sim.Elapsed)} {Glyphs.Dot} journal {sim.JournalId} {Glyphs.Dot} run {sim.RunNumber} {Glyphs.Dot} exit code {exit}", S.Dim));
        lines.Add(Fmt.Tally(root, words: true));

        var failed = sim.Tasks.Where(t => t.Status == Status.Failed).ToList();
        var cancelled = sim.Tasks.Where(t => t.Status == Status.Cancelled).ToList();
        var unreached = root.Count(Status.Pending) + root.Count(Status.Ready);
        if (unreached > 0)
        {
            lines.Add(new Line().Add($"{Fmt.Num(unreached)} tasks were not reached", S.Dim));
        }

        if (failed.Count > 0)
        {
            lines.Add(new Line());
            lines.Add(new Line().Add($"Failures ({failed.Count})", S.Bold));
            foreach (var task in failed)
            {
                var facts = $"{(task.Unexpected ? "unexpected failure" : "reported failure")} {Glyphs.Dot} {Fmt.Clock(task.SettledAt!.Value)} {Glyphs.Dot} ran {Fmt.Dur(TimeSpan.FromSeconds(task.Elapsed))}";
                if (task.Total is { } total && task.Current is { } current)
                {
                    facts += $" {Glyphs.Dot} got to {Fmt.Num(current)} of {Fmt.Num(total)}";
                }

                lines.Add(new Line().Add(Glyphs.For(Status.Failed) + " ", S.BoldRed).Add(task.Path, S.BoldRed).Add("  " + facts, S.Dim));
                lines.Add(new Line().Add("    " + task.Message, S.Red));
                foreach (var detail in task.Details)
                {
                    lines.Add(new Line().Add("    " + detail));
                }

                if (task.DetailsDropped > 0)
                {
                    lines.Add(new Line().Add($"    and {Fmt.Num(task.DetailsDropped)} more lines that were counted but not kept", S.Dim));
                }

                if (task.BlocksCount > 0)
                {
                    var behind = new List<string>();
                    BlockedBehind(root, task, behind);
                    lines.Add(new Line().Add($"    {Glyphs.For(Status.Blocked)} blocked {task.BlocksCount} tasks: ", S.Maroon).Add(string.Join(", ", behind), S.Dim));
                }
            }
        }

        if (cancelled.Count > 0)
        {
            lines.Add(new Line());
            lines.Add(new Line().Add($"Cancelled ({cancelled.Count})", S.Bold));
            foreach (var task in cancelled)
            {
                lines.Add(new Line().Add(Glyphs.For(Status.Cancelled) + " ", S.Yellow).Add(task.Path, S.Yellow)
                    .Add(string.IsNullOrEmpty(task.Message) ? "" : "  " + task.Message, S.Dim));
            }
        }

        var warned = sim.Tasks.Where(t => t.Warnings.Count > 0).ToList();
        var thisRun = warned.Where(t => !t.Earlier).ToList();
        var earlier = warned.Where(t => t.Earlier).ToList();
        if (warned.Count > 0)
        {
            lines.Add(new Line());
            lines.Add(new Line().Add($"Warnings ({Fmt.Num(root.WarningCount)} on {warned.Count} tasks)", S.Bold));
            foreach (var task in thisRun)
            {
                lines.Add(new Line().Add(Glyphs.Warning + " ", S.Yellow).Add(task.Path, S.Yellow)
                    .Add($"  {Fmt.Num(task.Warnings.Count + task.WarningsDropped)}", S.Dim));
                foreach (var warning in task.Warnings)
                {
                    lines.Add(new Line().Add("    " + warning));
                }

                if (task.WarningsDropped > 0)
                {
                    lines.Add(new Line().Add($"    and {Fmt.Num(task.WarningsDropped)} more that were counted but not kept", S.Dim));
                }
            }

            if (earlier.Count > 0)
            {
                var count = earlier.Sum(t => t.Warnings.Count + t.WarningsDropped);
                lines.Add(new Line().Add($"{Glyphs.Warning} {Fmt.Num(count)} warnings on {earlier.Count} tasks came from earlier runs and are not listed again", S.Dim));
            }
        }

        if (sim.Outcome != "complete")
        {
            var again = root.TaskCount - root.Count(Status.Succeeded);
            lines.Add(new Line());
            lines.Add(new Line().Add($"A run that continues journal {sim.JournalId} has {Fmt.Num(again)} tasks left to run.", S.Dim));
        }

        return lines;
    }

    /// <summary>Names what a failure blocked at the highest level that is blocked as a whole.</summary>
    private static void BlockedBehind(Node node, Node cause, List<string> output)
    {
        foreach (var child in node.Children)
        {
            if (child.IsGroup)
            {
                if (child.TaskCount > 0 && child.Count(Status.Blocked) == child.TaskCount && child.FirstFailure == cause)
                {
                    output.Add($"{child.Path} ({child.TaskCount})");
                }
                else if (child.Count(Status.Blocked) > 0)
                {
                    BlockedBehind(child, cause, output);
                }
            }
            else if (child.Status == Status.Blocked && child.BlockedBy == cause)
            {
                output.Add(child.Path);
            }
        }
    }
}
