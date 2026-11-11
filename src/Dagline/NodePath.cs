namespace Dagline;

/// <summary>
/// The identity of a node: its segments from the root joined by <c>/</c>, as in <c>shard-3/tenant-a/prepare</c>.
/// The same path means the same work in every run.
/// </summary>
/// <remarks>
/// Two paths are equal when their text is equal, compared ordinally, so <c>a/B</c> and <c>a/b</c> are different paths.
/// The <see langword="default"/> value is not a path of any node: it has no segments and prints as an empty string.
/// </remarks>
public readonly record struct NodePath
{
    private readonly string? _text;

    private NodePath(string text) => _text = text;

    /// <summary>Returns the segments joined by <c>/</c>.</summary>
    /// <returns>The path as text.</returns>
    public override string ToString() => _text ?? string.Empty;

    internal static NodePath AtRoot(string segment) => new(segment);
}
