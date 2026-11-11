namespace Dagline;

/// <summary>What one change made to a task instance: the task as it is just after the change, and the event the change produced.</summary>
/// <param name="Task">The task as a snapshot shows it just after the change.</param>
/// <param name="Event">The event the change produced, or <see langword="null"/> when it produced none.</param>
internal readonly record struct TaskChange(NodeSnapshot Task, RunEvent? Event);
