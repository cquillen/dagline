namespace Dagline;

/// <summary>What one change made to a task instance: the task as it is just after the change, and the event the change produced.</summary>
/// <param name="Task">
/// The task as a snapshot shows it just after the change, when the event carries it; <see langword="null"/> when the change's
/// snapshot is to be taken from the instance once the change is closed. A task may report progress millions of times, and only
/// the last report before a snapshot is taken is ever read, so a report builds no node of its own.
/// </param>
/// <param name="Event">The event the change produced, or <see langword="null"/> when it produced none.</param>
internal readonly record struct TaskChange(NodeSnapshot? Task, RunEvent? Event);
