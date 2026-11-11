namespace Dagline;

/// <summary>
/// The name of a limit. This is the one home of how limit names compare: two names are one limit when their text
/// is equal, compared ordinally, so <c>database</c> and <c>Database</c> are two limits.
/// </summary>
/// <remarks>
/// The public API takes and gives a limit name as a <see cref="string"/>. Inside Dagline a name is one of these
/// from the moment a node names it or the options size it, so no collection of names needs to be told how to compare them.
/// </remarks>
/// <param name="Text">The name as the consumer wrote it.</param>
internal readonly record struct LimitName(string Text)
{
    /// <summary>
    /// How the text of two names compares, for a collection that a consumer reads and that is therefore keyed by the text.
    /// </summary>
    public static StringComparer TextComparer => StringComparer.Ordinal;

    public bool Equals(LimitName other) => TextComparer.Equals(Text, other.Text);

    public override int GetHashCode() => TextComparer.GetHashCode(Text);

    public override string ToString() => Text;
}
