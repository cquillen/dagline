namespace Dagline;

/// <summary>The one home of what is done with the line breaks in a text a task gives.</summary>
internal static class LineBreaks
{
    /// <summary>
    /// Text as one line: each line break in it is replaced by a space. A warning is kept that way, and status text,
    /// which is kept as given, is printed that way.
    /// </summary>
    public static string OneLine(string text) => text.ReplaceLineEndings(" ");

    /// <summary>
    /// Text a task reports as a message or waiting text, as the run keeps it: one line, and <see langword="null"/> for
    /// empty text or none.
    /// </summary>
    public static string? OneLineOrNone(string? text) => string.IsNullOrEmpty(text) ? null : OneLine(text);

    /// <summary>Text given as detail is one detail line for each line of it: it is split at every line break.</summary>
    public static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');
}
