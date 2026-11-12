using System.Globalization;
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
    public const string Rule = "an id is the UTC creation time as yyyyMMddTHHmmssZ, a hyphen and six lowercase hex digits, "
        + "as in 20261111T041200Z-a3f9c2";

    private const string _extension = ".journal";

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

    /// <summary>The path of the file of the journal <paramref name="id"/> in <paramref name="directory"/>.</summary>
    public static string FileIn(string directory, string id) => Path.Combine(directory, id + _extension);

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Format();
}
