namespace Dagline;

/// <summary>
/// The one home of what each thing a task reports about itself does to the task as a snapshot shows it.
/// </summary>
internal static class TaskReportRules
{
    public static NodeSnapshot WithStatusText(this NodeSnapshot task, string? text) => task with { StatusText = text };

    public static NodeSnapshot WithCount(this NodeSnapshot task, long current, long? total)
        => task with { Count = current, Total = total };
}
