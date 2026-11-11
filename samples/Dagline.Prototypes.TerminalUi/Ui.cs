// PROTOTYPE. Shared drawing helpers: a pre-measured line of styled text, glyphs, and formatting.
// Every layout builds a list of lines that already fit the terminal, because the live display cannot scroll or wrap for us.

using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dagline.Prototypes.TerminalUi;

internal static class S
{
    public static readonly Style None = Style.Plain;
    public static readonly Style Bold = Style.Parse("bold");
    public static readonly Style Dim = Style.Parse("grey");
    public static readonly Style Green = Style.Parse("green");
    public static readonly Style DimGreen = Style.Parse("darkgreen");
    public static readonly Style Blue = Style.Parse("dodgerblue1");
    public static readonly Style Cyan = Style.Parse("darkcyan");
    public static readonly Style Yellow = Style.Parse("yellow");
    public static readonly Style Red = Style.Parse("red");
    public static readonly Style BoldRed = Style.Parse("bold red");
    public static readonly Style Maroon = Style.Parse("maroon");
    public static readonly Style Bar = Style.Parse("black on yellow");
    public static readonly Style BarDim = Style.Parse("yellow");
}

internal sealed class Line
{
    private readonly List<(string Text, Style Style)> _spans = [];

    public int Length { get; private set; }

    public Line Add(string text, Style? style = null)
    {
        if (text.Length > 0)
        {
            _spans.Add((text, style ?? S.None));
            Length += text.Length;
        }

        return this;
    }

    public Line Add(Line other)
    {
        foreach (var (text, style) in other._spans)
        {
            Add(text, style);
        }

        return this;
    }

    public Line PadTo(int width) => Length < width ? Add(new string(' ', width - Length)) : this;

    public Line Fit(int width)
    {
        if (Length <= width)
        {
            return this;
        }

        var fitted = new Line();
        var room = Math.Max(0, width - 1);
        foreach (var (text, style) in _spans)
        {
            if (room <= 0)
            {
                break;
            }

            var part = text.Length <= room ? text : text[..room];
            fitted.Add(part, style);
            room -= part.Length;
        }

        return width > 0 ? fitted.Add(Glyphs.Ellipsis, S.Dim) : fitted;
    }

    /// <summary>Left part, then the right part pushed to the right edge. The left part gives way when space is short.</summary>
    public static Line Split(Line left, Line right, int width)
    {
        var room = width - right.Length - 1;
        if (room < 8)
        {
            return left.Fit(width);
        }

        return left.Fit(room).PadTo(width - right.Length).Add(right);
    }

    public IRenderable ToRenderable()
    {
        var paragraph = new Paragraph();
        foreach (var (text, style) in _spans)
        {
            paragraph.Append(text, style);
        }

        if (_spans.Count == 0)
        {
            paragraph.Append(" ", S.None);
        }

        return paragraph;
    }

    public string Plain()
    {
        var sb = new StringBuilder();
        foreach (var (text, _) in _spans)
        {
            sb.Append(text);
        }

        return sb.ToString();
    }
}

internal static class Glyphs
{
    public static bool Unicode = true;

    public static string Ellipsis => Unicode ? "…" : "~";
    public static string Warning => Unicode ? "▲" : "!";
    public static string Waiting => Unicode ? "◌" : "~";
    public static string BarOn => Unicode ? "━" : "=";
    public static string BarOff => Unicode ? "─" : "-";
    public static string Rule => Unicode ? "─" : "-";
    public static string Dot => Unicode ? "·" : "|";

    public static string For(Status status) => status switch
    {
        Status.Pending => Unicode ? "·" : ".",
        Status.Ready => Unicode ? "○" : "o",
        Status.Running => Unicode ? "●" : "*",
        Status.Succeeded => Unicode ? "✓" : "+",
        Status.Failed => Unicode ? "✗" : "X",
        Status.Blocked => Unicode ? "⊘" : "-",
        _ => Unicode ? "■" : "#",
    };
}

internal static class Fmt
{
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    public static string Num(long n) => n.ToString("N0", s_inv);

    public static string Dur(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    public static string Clock(DateTimeOffset t) => t.ToString("HH:mm:ss", s_inv);

    public static Style StyleOf(Status status, bool earlier = false, bool waiting = false) => status switch
    {
        Status.Pending => S.Dim,
        Status.Ready => S.None,
        Status.Running => waiting ? S.Cyan : S.Blue,
        Status.Succeeded => earlier ? S.DimGreen : S.Green,
        Status.Failed => S.BoldRed,
        Status.Blocked => S.Maroon,
        _ => S.Yellow,
    };

    /// <summary>The glyph and colour of a node. A running group that is only waiting reads as waiting.</summary>
    public static (string Glyph, Style Style) Mark(Node node)
    {
        var status = node.Derived;
        var waiting = status == Status.Running && (node.IsGroup ? node.Working == 0 : node.IsWaiting);
        var earlier = status == Status.Succeeded && node.EarlierCount == node.TaskCount && (node.IsGroup || node.Earlier);
        var glyph = waiting ? Glyphs.Waiting : Glyphs.For(status);
        return (glyph, StyleOf(status, earlier, waiting));
    }

    public static Line Bar(long current, long total, int width, Style on)
    {
        var filled = total <= 0 ? 0 : (int)Math.Clamp(current * width / total, 0, width);
        return new Line().Add(Repeat(Glyphs.BarOn, filled), on).Add(Repeat(Glyphs.BarOff, width - filled), S.Dim);
    }

    /// <summary>One bar for the whole run, split by status in the order work moves through them.</summary>
    public static Line StackedBar(Node node, int width)
    {
        var line = new Line();
        if (node.TaskCount == 0 || width <= 0)
        {
            return line;
        }

        var earlier = node.EarlierCount;
        (int Count, Style Style)[] parts =
        [
            (earlier, S.DimGreen),
            (node.Count(Status.Succeeded) - earlier, S.Green),
            (node.Count(Status.Failed), S.BoldRed),
            (node.Count(Status.Blocked), S.Maroon),
            (node.Count(Status.Cancelled), S.Yellow),
            (node.Working, S.Blue),
            (node.WaitingCount, S.Cyan),
        ];

        var used = 0;
        foreach (var (count, style) in parts)
        {
            if (count == 0)
            {
                continue;
            }

            var cells = Math.Max(1, (int)Math.Round((double)count * width / node.TaskCount));
            cells = Math.Min(cells, width - used);
            line.Add(Repeat(Glyphs.BarOn, cells), style);
            used += cells;
        }

        return line.Add(Repeat(Glyphs.BarOff, width - used), S.Dim);
    }

    public static string Repeat(string s, int n) => n <= 0 ? "" : string.Concat(Enumerable.Repeat(s, n));

    /// <summary>Counts per status for a node, leaving out the zeros.</summary>
    public static Line Tally(Node node, bool words)
    {
        var line = new Line();
        void Part(int count, string glyph, Style style, string word, string? extra = null)
        {
            if (count == 0)
            {
                return;
            }

            if (line.Length > 0)
            {
                line.Add("  ");
            }

            line.Add($"{glyph} {Num(count)}", style);
            if (words)
            {
                line.Add(" " + word, S.Dim);
            }

            if (extra is not null)
            {
                line.Add(" " + extra, S.Dim);
            }
        }

        Part(node.Count(Status.Succeeded), Glyphs.For(Status.Succeeded), S.Green, "succeeded",
            node.EarlierCount > 0 ? $"({Num(node.EarlierCount)} earlier)" : null);
        Part(node.Working, Glyphs.For(Status.Running), S.Blue, "working");
        Part(node.WaitingCount, Glyphs.Waiting, S.Cyan, "waiting");
        Part(node.Count(Status.Ready), Glyphs.For(Status.Ready), S.None, "ready");
        Part(node.Count(Status.Pending), Glyphs.For(Status.Pending), S.Dim, "pending");
        Part(node.Count(Status.Failed), Glyphs.For(Status.Failed), S.BoldRed, "failed");
        Part(node.Count(Status.Blocked), Glyphs.For(Status.Blocked), S.Maroon, "blocked");
        Part(node.Count(Status.Cancelled), Glyphs.For(Status.Cancelled), S.Yellow, "cancelled");
        Part(node.WarningCount, Glyphs.Warning, S.Yellow, node.WarningCount == 1 ? "warning" : "warnings");
        return line;
    }

    public static Line? StopBanner(Sim sim, int width)
    {
        if (sim.StopRequestedAt is null)
        {
            return null;
        }

        var left = sim.StopDeadline!.Value - sim.Now;
        return new Line()
            .Add(" STOPPING ", Style.Parse("black on yellow"))
            .Add($" waiting for {sim.Running.Count} running tasks to end", S.Yellow)
            .Add($" {Glyphs.Dot} {Dur(left)} left {Glyphs.Dot} Ctrl+C again to stop waiting", S.Dim)
            .Fit(width);
    }

    public static Line Rule(string title, int width, Line? right = null)
    {
        var line = new Line().Add(Glyphs.Rule + Glyphs.Rule + " ", S.Dim).Add(title, S.Bold).Add(" ");
        if (right is not null)
        {
            line.Add(right).Add(" ");
        }

        return line.Add(Repeat(Glyphs.Rule, width - line.Length), S.Dim).Fit(width);
    }

    /// <summary>What a running task says about itself, in the space given: bar, count and status text.</summary>
    public static Line Progress(Node task, int barWidth)
    {
        var line = new Line();
        if (task.IsWaiting)
        {
            return line.Add(task.ProgressText ?? "", S.Cyan);
        }

        if (task.Total is { } total && task.Current is { } current)
        {
            line.Add(Bar(current, total, barWidth, S.Blue)).Add($" {Num(current)} of {Num(total)}  ");
        }
        else if (task.Current is { } tally)
        {
            line.Add($"{Num(tally)}  ");
        }

        return line.Add(task.ProgressText ?? "", S.Dim);
    }

    public static string Since(Sim sim, Node task) =>
        task.IsWaiting ? "waiting " + Dur(sim.Now - task.WaitingSince!.Value) : Dur(TimeSpan.FromSeconds(task.Elapsed));

    /// <summary>A path that keeps its end when it has to be shortened, because the end names the task.</summary>
    public static string TailPath(string path, int width)
    {
        if (path.Length <= width)
        {
            return path;
        }

        return width <= 1 ? Glyphs.Ellipsis : Glyphs.Ellipsis + path[^(width - 1)..];
    }
}
