namespace Dagline;

/// <summary>
/// What a run gives a task body: the task's identity and the run's clock.
/// </summary>
/// <remarks>A run creates one context for each task it starts. A context is safe to use from several threads at once.</remarks>
public sealed class TaskContext
{
    internal TaskContext()
    {
    }
}
