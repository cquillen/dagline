using System.Globalization;
using static System.FormattableString;

namespace Dagline.Spectre;

/// <summary>The one home of how the display and the summary write numbers, durations and progress, and space out a line.</summary>
internal static class DisplayText
{
    /// <summary>The space between two parts of a line, and before the line that says what a pane left out.</summary>
    public const string Between = "  ";

    /// <summary>The space before a line that belongs to the line above it, such as a failure's message.</summary>
    public const string Indent = "    ";

    /// <summary>A whole number with its thousands grouped, as in <c>3,173</c>, in the invariant culture.</summary>
    public static string Number(long number) => number.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A number and the word it counts, as in <c>1 task</c> and <c>3,173 tasks</c>.</summary>
    public static string Count(long number, string one, string many) => $"{Number(number)} {(number == 1 ? one : many)}";

    /// <summary>The time of day in UTC to the second, as in <c>06:48:40</c>.</summary>
    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Status text on one line: each line break is printed as a space, as the plain-text reporter prints it. A message and
    /// waiting text already arrive as one line.
    /// </summary>
    /// <remarks>
    /// It is a copy of the core's internal <c>LineBreaks.OneLine</c>: the package boundary keeps that rule internal. Status text
    /// keeps its line breaks in the snapshot, as #98 decided after measuring the cost of making it one line where a task
    /// reports it, so each reporter flattens it as it prints.
    /// </remarks>
    public static string OneLine(string text) => text.ReplaceLineEndings(" ");

    /// <summary>
    /// Whole seconds under a minute, as in <c>45s</c>. Minutes and seconds under an hour, as in <c>20m 44s</c>. Hours and minutes
    /// from there, as in <c>1h 15m</c>. Always rounded down, and a duration below zero is written as zero.
    /// </summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration < TimeSpan.FromMinutes(1))
        {
            return Invariant($"{duration.Seconds}s");
        }

        return duration < TimeSpan.FromHours(1)
            ? Invariant($"{duration.Minutes}m {duration.Seconds}s")
            : Invariant($"{(long)duration.TotalHours}h {duration.Minutes}m");
    }

    /// <summary>
    /// How far a task's count has got, as in <c>1,928 of 4,087</c>, or the count alone when it came with no total.
    /// <see langword="null"/> when the task has given no count.
    /// </summary>
    public static string? Progress(NodeSnapshot task) => task switch
    {
        { Count: { } count, Total: { } total } => $"{Number(count)} of {Number(total)}",
        { Count: { } count } => Number(count),
        _ => null,
    };

    /// <summary><paramref name="glyph"/> <paramref name="times"/> times over, or nothing when <paramref name="times"/> is not above zero.</summary>
    public static string Repeat(string glyph, int times) => times <= 0 ? "" : string.Concat(Enumerable.Repeat(glyph, times));
}
