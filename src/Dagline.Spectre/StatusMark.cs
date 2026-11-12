using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>A glyph and the style it is drawn in.</summary>
internal readonly record struct Mark(string Glyph, Style Style)
{
    /// <summary>The glyph in its style, then <paramref name="after"/> in the same style.</summary>
    public StyledLine Line(string after = "") => StyledLine.Of(Glyph + after, Style);
}

/// <summary>
/// The one home of how a status is drawn: its glyph in its style. Every pane, the header's tally and the summary take their
/// marks from here, so a status looks the same wherever it is shown.
/// </summary>
internal static class StatusMark
{
    /// <summary>
    /// The mark of <paramref name="status"/>. A running task that is waiting is drawn with <see cref="Waiting"/> instead, and an
    /// earlier success with <see cref="EarlierSuccess"/>: the caller knows which tasks those are.
    /// </summary>
    public static Mark For(NodeStatus status, Glyphs glyphs) => status switch
    {
        NodeStatus.Pending => new(glyphs.Pending, Palette.Pending),
        NodeStatus.Ready => new(glyphs.Ready, Palette.Ready),
        NodeStatus.Running => new(glyphs.Running, Palette.Working),
        NodeStatus.Succeeded => new(glyphs.Succeeded, Palette.Succeeded),
        NodeStatus.Failed => new(glyphs.Failed, Palette.Failed),
        NodeStatus.Blocked => new(glyphs.Blocked, Palette.Blocked),
        NodeStatus.Cancelled => new(glyphs.Cancelled, Palette.Cancelled),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a status."),
    };

    /// <summary>A running task that is waiting on an external system.</summary>
    public static Mark Waiting(Glyphs glyphs) => new(glyphs.Waiting, Palette.Waiting);

    /// <summary>A success from an earlier run: the success glyph, dimmer.</summary>
    public static Mark EarlierSuccess(Glyphs glyphs) => new(glyphs.Succeeded, Palette.EarlierSuccess);

    /// <summary>A warning.</summary>
    public static Mark Warning(Glyphs glyphs) => new(glyphs.Warning, Palette.Warning);
}
