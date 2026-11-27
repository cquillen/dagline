using System.Collections;

namespace Dagline;

/// <summary>
/// An immutable list that is cheap to change in one place: its elements are held in chunks of one size,
/// and a list made from another with <see cref="With"/> shares every chunk but those that hold the changes.
/// </summary>
/// <remarks>
/// <para>
/// A run hands a snapshot of every node to its reporters with every event, and a reporter that is behind keeps each one
/// in its queue. Were each a copy of every node, a run would copy, and could hold on to, the square of its number of nodes.
/// </para>
/// <para>
/// A chunk holds about as many elements as there are chunks, the square root of the count, so one change copies
/// about twice that many references: the chunk it is in, and the list of chunks. Changes made together copy the list of chunks
/// once, and each chunk they touch once, so a task and the groups above it, which often share a chunk, cost little more than one.
/// </para>
/// <para>
/// Nothing in a list changes once it is made, so it is safe to read from any thread.
/// </para>
/// </remarks>
internal sealed class ChunkedList<T> : IReadOnlyList<T>
{
    private const int _smallestChunk = 16;

    private readonly T[][] _chunks;
    private readonly int _chunkSize;

    private ChunkedList(T[][] chunks, int chunkSize, int count)
    {
        _chunks = chunks;
        _chunkSize = chunkSize;
        Count = count;
    }

    public int Count { get; }

    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is negative, or is not less than <see cref="Count"/>.
    /// </exception>
    public T this[int index]
    {
        get
        {
            ThrowIfOutOfRange(index);

            return _chunks[index / _chunkSize][index % _chunkSize];
        }
    }

    /// <summary>A list of the elements given, in the order given.</summary>
    public static ChunkedList<T> From(IEnumerable<T> elements)
    {
        T[] all = [.. elements];
        var chunkSize = Math.Max(_smallestChunk, (int)Math.Ceiling(Math.Sqrt(all.Length)));

        return new([.. all.Chunk(chunkSize)], chunkSize, all.Length);
    }

    /// <summary>
    /// A list that differs from this one in the elements at the positions given, each set in turn, so a later change to a position
    /// wins. This list is not changed, and the two share every chunk but those that hold the positions. No change gives this list.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An index is negative, or is not less than <see cref="Count"/>.
    /// </exception>
    public ChunkedList<T> With(ReadOnlySpan<(int Index, T Element)> changes)
    {
        if (changes.IsEmpty)
        {
            return this;
        }

        T[][] chunks = [.. _chunks];
        foreach (var (index, element) in changes)
        {
            ThrowIfOutOfRange(index);
            var changed = index / _chunkSize;
            if (ReferenceEquals(chunks[changed], _chunks[changed]))
            {
                chunks[changed] = [.. _chunks[changed]];
            }

            chunks[changed][index % _chunkSize] = element;
        }

        return new(chunks, _chunkSize, Count);
    }

    public IEnumerator<T> GetEnumerator() => _chunks.SelectMany(chunk => chunk).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void ThrowIfOutOfRange(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
    }
}
