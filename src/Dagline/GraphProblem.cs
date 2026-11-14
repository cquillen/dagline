namespace Dagline;

/// <summary>
/// One reason a graph could not be built, as listed on <see cref="GraphException.Problems"/>.
/// </summary>
public sealed record GraphProblem
{
    internal GraphProblem(NodePath? path, string message)
    {
        Path = path;
        Message = message;
    }

    /// <summary>
    /// The path of the node the problem is about. For a problem with a dependency it is the dependent node,
    /// and for a cycle the first node the message names.
    /// <see langword="null"/> when that node has no valid path, because its segment or that of a group that holds it is invalid.
    /// </summary>
    public NodePath? Path { get; }

    /// <summary>
    /// One line that says what is wrong, naming every node involved by its path. It is written for a person to read:
    /// the wording may change between releases.
    /// </summary>
    public string Message { get; }
}
