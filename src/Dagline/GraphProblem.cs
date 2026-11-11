namespace Dagline;

/// <summary>
/// One reason a graph could not be built, as listed on <see cref="GraphException.Problems"/>.
/// </summary>
/// <param name="Path">
/// The path of the node the problem is about. For a problem with a dependency it is the dependent node,
/// and for a cycle the first node the message names.
/// <see langword="null"/> when that node has no valid path, because its segment or that of a group that holds it is invalid.
/// </param>
/// <param name="Message">
/// One line that says what is wrong, naming every node involved by its path. It is written for a person to read:
/// the wording may change between releases.
/// </param>
public sealed record GraphProblem(NodePath? Path, string Message)
{
    /// <summary>
    /// One line that says what is wrong, naming every node involved by its path. It is written for a person to read:
    /// the wording may change between releases.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value given is <see langword="null"/>.</exception>
    public string Message { get; init => field = Arguments.NotNull(value); } = Arguments.NotNull(Message);
}
