namespace Dagline;

/// <summary>The one home of what is done with the line breaks in a text a task gives.</summary>
internal static class LineBreaks
{
    /// <summary>
    /// Text as one line: each line break in it is replaced by a space. A message and a warning are kept that way,
    /// and status text and waiting text, which are kept as given, are printed that way.
    /// </summary>
    public static string OneLine(string text) => text.ReplaceLineEndings(" ");

    /// <summary>Text given as detail is one detail line for each line of it: it is split at every line break.</summary>
    public static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');
}
