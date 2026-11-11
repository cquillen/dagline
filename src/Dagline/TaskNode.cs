namespace Dagline;

/// <summary>
/// A handle to a task of a graph that is being built. A task is a unit of work that runs consumer code.
/// </summary>
/// <remarks>
/// Every method returns the same handle, so declarations chain: <c>builder.Task("publish", Publish).Requires(prepare)</c>.
/// </remarks>
public sealed class TaskNode : Node
{
    internal TaskNode(GraphBuilder builder, NodePath path, TaskBody body)
        : base(builder, path)
    {
        Body = body;
    }

    internal TaskBody Body { get; }

    /// <summary>
    /// Declares that this task <em>requires</em> <paramref name="upstream"/>: it becomes ready only once the upstream has succeeded.
    /// </summary>
    /// <param name="upstream">The node this task requires.</param>
    /// <returns>This handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="upstream"/> is <see langword="null"/>.</exception>
    public TaskNode Requires(Node upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        Builder.Declare(this, upstream, DependencyKind.Requires);
        return this;
    }
}
