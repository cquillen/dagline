using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Dagline;

/// <summary>
/// The one home of a journal's id: the UTC creation time as <c>yyyyMMddTHHmmssZ</c>, a hyphen and six random lowercase hex digits,
/// as in <c>20261111T041200Z-a3f9c2</c>. The id sorts by age, can be typed, and names the journal's file.
/// </summary>
internal static partial class JournalId
{
    /// <summary>The id format in words, worded to end a sentence that names an id that breaks it.</summary>
    private const string _rule = "an id is the UTC creation time as yyyyMMddTHHmmssZ, a hyphen and six lowercase hex digits, "
        + "as in 20261111T041200Z-a3f9c2";

    /// <summary>The extension of a journal's file.</summary>
    public const string Extension = ".journal";

    /// <summary>A new id for a journal created at <paramref name="created"/>, with a newly drawn suffix.</summary>
    public static string New(DateTimeOffset created)
        => created.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
            + "-"
            + RandomNumberGenerator.GetHexString(6, lowercase: true);

    /// <summary>
    /// Whether <paramref name="id"/> is in the id format. Only such an id is ever joined to a directory,
    /// so that an id can never name a file outside it.
    /// </summary>
    public static bool IsValid(string id) => Format().IsMatch(id);

    /// <summary>Refuses an <paramref name="id"/> given at a public call that is not in the id format, as an argument error.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not in the id format.</exception>
    public static void ThrowIfNotValid(string id, [CallerArgumentExpression(nameof(id))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(id, paramName);
        if (!IsValid(id))
        {
            throw new ArgumentException($"The journal id '{id}' is not in the id format: {_rule}.", paramName);
        }
    }

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Format();
}
