using System.Buffers;

namespace Dagline;

/// <summary>
/// The identity of a node: its segments from the root joined by <c>/</c>, as in <c>shard-3/tenant-a/prepare</c>.
/// The same path means the same work in every run.
/// </summary>
/// <remarks>
/// <para>
/// A path has one or more segments, and a segment is one or more of the characters
/// <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>, <c>.</c>, <c>_</c> and <c>-</c>.
/// </para>
/// <para>
/// Two paths are equal when their text is equal, compared ordinally, so <c>a/B</c> and <c>a/b</c> are different paths.
/// Paths have no order of their own: the only order Dagline uses is declaration order.
/// </para>
/// <para>
/// The <see langword="default"/> value is not the path of any node: it has no segments and prints as an empty string.
/// </para>
/// </remarks>
public readonly record struct NodePath
{
    private const char _separator = '/';

    private static readonly SearchValues<char> _segmentCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-");

    private readonly string? _text;

    private NodePath(string text) => _text = text;

    /// <summary>The segments of the path, from the root down. The last one names the node among its siblings.</summary>
    public IReadOnlyList<string> Segments => _text is null ? [] : _text.Split(_separator);

    /// <summary>The path of the group that holds the node, or <see langword="null"/> for a node at the root.</summary>
    public NodePath? Parent
    {
        get
        {
            var lastSeparator = _text?.LastIndexOf(_separator) ?? -1;
            return lastSeparator < 0 ? null : new NodePath(_text![..lastSeparator]);
        }
    }

    /// <summary>Reads a path from its text form, the form <see cref="ToString"/> writes.</summary>
    /// <param name="text">Segments joined by <c>/</c>.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">
    /// <paramref name="text"/> is not a valid path: it is empty, has an empty segment, or has a character a segment may not use.
    /// </exception>
    public static NodePath Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return TryParse(text, out var path)
            ? path
            : throw new FormatException(
                $"'{text}' is not a valid path. A path is one or more segments joined by '/', and a segment is one or more of A-Z a-z 0-9 . _ -");
    }

    /// <summary>Reads a path from its text form without throwing.</summary>
    /// <param name="text">Segments joined by <c>/</c>, or <see langword="null"/>.</param>
    /// <param name="path">The path when the method returns <see langword="true"/>, and the <see langword="default"/> value otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a valid path.</returns>
    public static bool TryParse(string? text, out NodePath path)
    {
        var isPath = text is not null && text.Split(_separator).All(IsSegment);
        path = isPath ? new NodePath(text!) : default;
        return isPath;
    }

    /// <summary>Returns the segments joined by <c>/</c>.</summary>
    /// <returns>The path as text.</returns>
    public override string ToString() => _text ?? string.Empty;

    internal static NodePath AtRoot(string segment) => new(segment);

    private static bool IsSegment(string segment)
        => segment.Length > 0 && !segment.AsSpan().ContainsAnyExcept(_segmentCharacters);
}
