namespace Dagline;

/// <summary>
/// What a run gives a task body: the task's identity and the run's clock.
/// </summary>
/// <remarks>
/// A run creates one context for each task it starts, so a graph that is run twice gives each run's bodies their own contexts.
/// A context is safe to use from several threads at once.
/// </remarks>
public sealed class TaskContext
{
    internal TaskContext(NodePath path, TimeProvider timeProvider)
    {
        Path = path;
        TimeProvider = timeProvider;
    }

    /// <summary>The path of the task this body is running as.</summary>
    public NodePath Path { get; }

    /// <summary>
    /// The clock of the run, as given in <see cref="RunOptions.TimeProvider"/>.
    /// Take any delay or timestamp of the task's own from it, so that a test of the task controls time.
    /// </summary>
    public TimeProvider TimeProvider { get; }
}
