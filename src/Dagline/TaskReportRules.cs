namespace Dagline;

/// <summary>
/// The one home of what each thing a task reports about itself does to the task as a snapshot shows it.
/// </summary>
internal static class TaskReportRules
{
    /// <summary>How many warnings, and separately how many detail lines, are kept for one task. The rest are counted.</summary>
    private const int Kept = 100;

    /// <summary>
    /// Adds a warning to the task when it has fewer than the number kept, and otherwise counts it as omitted.
    /// A warning that was kept is one more entry in <see cref="NodeSnapshot.Warnings"/>.
    /// </summary>
    public static NodeSnapshot WithWarning(this NodeSnapshot task, string warning)
        => task.Warnings.Count < Kept
            ? task with { Warnings = [.. task.Warnings, OneLine(warning)] }
            : task with { WarningsOmitted = task.WarningsOmitted + 1 };

    public static NodeSnapshot WithStatusText(this NodeSnapshot task, string? text) => task with { StatusText = text };

    public static NodeSnapshot WithCount(this NodeSnapshot task, long current, long? total)
        => task with { Count = current, Total = total };

    /// <summary>A message or a warning is one line: each line break in the text is replaced by a space.</summary>
    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
