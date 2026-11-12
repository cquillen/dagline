namespace Dagline.Spectre;

/// <summary>The one home of the characters the display and the summary draw with: one per status, and the marks between.</summary>
/// <param name="Succeeded">A succeeded task.</param>
/// <param name="Failed">A failed task.</param>
/// <param name="Running">A running task that is working.</param>
/// <param name="Ready">A ready task.</param>
/// <param name="Waiting">A running task that is waiting on an external system.</param>
/// <param name="Pending">A pending task.</param>
/// <param name="Blocked">A blocked task.</param>
/// <param name="Cancelled">A cancelled task.</param>
/// <param name="Warning">A warning.</param>
/// <param name="Bar">A filled cell of a bar.</param>
/// <param name="BarRest">An empty cell of a bar.</param>
/// <param name="Rule">A cell of a horizontal rule.</param>
/// <param name="Ellipsis">Where text was cut.</param>
/// <param name="Separator">Between two facts on one line.</param>
internal sealed record Glyphs(
    string Succeeded,
    string Failed,
    string Running,
    string Ready,
    string Waiting,
    string Pending,
    string Blocked,
    string Cancelled,
    string Warning,
    string Bar,
    string BarRest,
    string Rule,
    string Ellipsis,
    string Separator)
{
    /// <summary>The default set.</summary>
    public static Glyphs Unicode { get; } = new("✓", "✗", "●", "○", "◌", "·", "⊘", "■", "▲", "━", "─", "─", "…", "·");

    /// <summary>The glyph of a status. A running task that is waiting is drawn with <see cref="Waiting"/> instead.</summary>
    public string For(NodeStatus status) => status switch
    {
        NodeStatus.Pending => Pending,
        NodeStatus.Ready => Ready,
        NodeStatus.Running => Running,
        NodeStatus.Succeeded => Succeeded,
        NodeStatus.Failed => Failed,
        NodeStatus.Blocked => Blocked,
        NodeStatus.Cancelled => Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a status."),
    };
}
