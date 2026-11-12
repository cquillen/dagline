namespace Dagline.Spectre;

/// <summary>The one home of how a status is drawn: its glyph in its style.</summary>
internal static class StatusMark
{
    /// <summary>
    /// The glyph of <paramref name="status"/> in its style. A running task that is waiting is drawn with
    /// <see cref="Glyphs.Waiting"/> and <see cref="Palette.Waiting"/> instead, and an earlier success with
    /// <see cref="Palette.EarlierSuccess"/>: the caller knows which tasks those are.
    /// </summary>
    public static StyledLine Of(NodeStatus status, Glyphs glyphs) => status switch
    {
        NodeStatus.Pending => StyledLine.Of(glyphs.Pending, Palette.Pending),
        NodeStatus.Ready => StyledLine.Of(glyphs.Ready, Palette.Ready),
        NodeStatus.Running => StyledLine.Of(glyphs.Running, Palette.Working),
        NodeStatus.Succeeded => StyledLine.Of(glyphs.Succeeded, Palette.Succeeded),
        NodeStatus.Failed => StyledLine.Of(glyphs.Failed, Palette.Failed),
        NodeStatus.Blocked => StyledLine.Of(glyphs.Blocked, Palette.Blocked),
        NodeStatus.Cancelled => StyledLine.Of(glyphs.Cancelled, Palette.Cancelled),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a status."),
    };
}
