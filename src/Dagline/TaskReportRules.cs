namespace Dagline;

/// <summary>
/// The one home of what each thing a task reports about itself does to the task as a snapshot shows it.
/// </summary>
internal static class TaskReportRules
{
    /// <summary>How many warnings, and separately how many detail lines, are kept for one task. The rest are counted.</summary>
    private const int _kept = 100;

    /// <summary>
    /// Adds a warning to the task when it has fewer than the number kept, and otherwise counts it as omitted.
    /// A warning that was kept is one more entry in <see cref="NodeSnapshot.Warnings"/>.
    /// </summary>
    public static NodeSnapshot WithWarning(this NodeSnapshot task, string warning)
        => task.Warnings.Count < _kept
            ? task with { Warnings = [.. task.Warnings, OneLine(warning)] }
            : task with { WarningsOmitted = task.WarningsOmitted + 1 };

    public static NodeSnapshot WithMessage(this NodeSnapshot task, string message) => task with { Message = OneLine(message) };

    /// <summary>
    /// Adds detail lines to the task, one for each line of each text, up to the number kept, and counts the rest as omitted.
    /// </summary>
    public static NodeSnapshot WithDetail(this NodeSnapshot task, IEnumerable<string> texts)
    {
        string[] lines = [.. texts.SelectMany(Lines)];
        var kept = Math.Min(lines.Length, _kept - task.Detail.Count);

        return task with
        {
            Detail = [.. task.Detail, .. lines.Take(kept)],
            DetailOmitted = task.DetailOmitted + lines.Length - kept,
        };
    }

    public static NodeSnapshot WithStatusText(this NodeSnapshot task, string? text) => task with { StatusText = text };

    public static NodeSnapshot WithCount(this NodeSnapshot task, long current, long? total)
        => task with { Count = current, Total = total };

    /// <summary>
    /// Text as one line: each line break in it is replaced by a space. A message and a warning are kept that way,
    /// and status text, which is kept as given, is printed that way.
    /// </summary>
    public static string OneLine(string text) => text.ReplaceLineEndings(" ");

    /// <summary>Text given as detail is one detail line for each line of it: it is split at every line break.</summary>
    public static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');
}
