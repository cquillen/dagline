namespace Dagline;

/// <summary>
/// A handle to a task of a graph that is being built. A task is a unit of work that runs consumer code.
/// </summary>
/// <remarks>
/// Every method returns the same handle, so declarations chain: <c>builder.Task("publish", Publish).Requires(prepare)</c>.
/// </remarks>
public sealed class TaskNode : Node
{
    internal TaskNode(GraphBuilder builder, GroupNode? parent, string segment, TaskBody body)
        : base(builder, parent, segment)
    {
        Body = body;
    }

    internal TaskBody Body { get; }

    /// <summary>Gives this task a title: a readable name for people.</summary>
    /// <param name="title">The title. It may hold any text and need not be unique. Calling again replaces it.</param>
    /// <returns>This handle.</returns>
    /// <remarks>
    /// A title is separate from the segment and never part of the path, so it can change between runs
    /// without changing which work the path means. It is carried to <see cref="NodeSnapshot.Title"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="title"/> is <see langword="null"/>.</exception>
    public TaskNode Title(string title)
    {
        SetTitle(title);
        return this;
    }

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
