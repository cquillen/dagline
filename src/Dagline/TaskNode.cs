namespace Dagline;

/// <summary>
/// A handle to a task of a graph that is being built. A task is a unit of work that runs consumer code.
/// </summary>
/// <remarks>
/// Every method returns the same handle, so declarations chain: <c>builder.Task("publish", Publish).Requires(prepare)</c>.
/// A task also has every dependency declared on a group that holds it.
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
    /// <exception cref="InvalidOperationException">The graph has been built.</exception>
    public TaskNode Title(string title)
    {
        SetTitle(title);
        return this;
    }

    /// <summary>
    /// Declares that this task <em>requires</em> <paramref name="upstream"/>: it becomes ready only once the upstream has succeeded.
    /// </summary>
    /// <param name="upstream">
    /// The node this task requires. A group stands for every task beneath it at any depth, so all of them must have succeeded.
    /// An empty group is met at once.
    /// </param>
    /// <returns>This handle.</returns>
    /// <remarks>
    /// Declaring the same dependency twice is the same as declaring it once. A task that ends up with both kinds on one upstream,
    /// declared on itself or on a group that holds it, <em>requires</em> that upstream.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="upstream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The graph has been built.</exception>
    public TaskNode Requires(Node upstream)
    {
        Declare(upstream, DependencyKind.Requires);
        return this;
    }

    /// <summary>
    /// Declares that this task <em>requires</em> every node in <paramref name="upstreams"/>, as if
    /// <see cref="Requires(Node)"/> had been called with each in turn.
    /// </summary>
    /// <param name="upstreams">
    /// The nodes this task requires. The collection is read once, during the call. An empty one declares nothing,
    /// so a dependency on a list that came back empty is valid.
    /// </param>
    /// <returns>This handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="upstreams"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A handle in <paramref name="upstreams"/> is <see langword="null"/>. Nothing is declared.</exception>
    /// <exception cref="InvalidOperationException">The graph has been built.</exception>
    public TaskNode Requires(IEnumerable<Node> upstreams)
    {
        Declare(upstreams, DependencyKind.Requires);
        return this;
    }

    /// <summary>
    /// Declares that this task runs <em>after</em> <paramref name="upstream"/>: it becomes ready once the upstream has settled,
    /// whatever the outcome.
    /// </summary>
    /// <param name="upstream">
    /// The node this task runs after. A group stands for every task beneath it at any depth, so all of them must have settled.
    /// An empty group is met at once.
    /// </param>
    /// <returns>This handle.</returns>
    /// <remarks>
    /// Declaring the same dependency twice is the same as declaring it once. A task that ends up with both kinds on one upstream,
    /// declared on itself or on a group that holds it, <em>requires</em> that upstream.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="upstream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The graph has been built.</exception>
    public TaskNode After(Node upstream)
    {
        Declare(upstream, DependencyKind.After);
        return this;
    }

    /// <summary>
    /// Declares that this task runs <em>after</em> every node in <paramref name="upstreams"/>, as if
    /// <see cref="After(Node)"/> had been called with each in turn.
    /// </summary>
    /// <param name="upstreams">
    /// The nodes this task runs after. The collection is read once, during the call. An empty one declares nothing,
    /// so a dependency on a list that came back empty is valid.
    /// </param>
    /// <returns>This handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="upstreams"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A handle in <paramref name="upstreams"/> is <see langword="null"/>. Nothing is declared.</exception>
    /// <exception cref="InvalidOperationException">The graph has been built.</exception>
    public TaskNode After(IEnumerable<Node> upstreams)
    {
        Declare(upstreams, DependencyKind.After);
        return this;
    }
}
