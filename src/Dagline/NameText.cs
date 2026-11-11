namespace Dagline;

/// <summary>The one home of how a status, a failure kind or a run outcome is named for people.</summary>
internal static class NameText
{
    /// <summary>The name in lower case, as in <c>succeeded</c>, <c>unexpected</c> or <c>incomplete</c>.</summary>
    public static string Of<T>(T value)
        where T : struct, Enum
        => value.ToString().ToLowerInvariant();
}
