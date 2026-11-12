using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>The one home of the styles the display and the summary draw with.</summary>
internal static class Palette
{
    public static Style Plain { get; } = Style.Plain;

    public static Style Bold { get; } = new(decoration: Decoration.Bold);

    public static Style Dim { get; } = new(Color.Grey);

    public static Style Succeeded { get; } = new(Color.Green);

    /// <summary>An earlier success: dimmer than a success of this run.</summary>
    public static Style EarlierSuccess { get; } = new(Color.DarkGreen);

    public static Style Failed { get; } = new(Color.Red, decoration: Decoration.Bold);

    public static Style Blocked { get; } = new(Color.Maroon);

    public static Style Cancelled { get; } = new(Color.Yellow);

    public static Style Working { get; } = new(Color.DodgerBlue1);

    public static Style Waiting { get; } = new(Color.DarkCyan);

    public static Style Warning { get; } = new(Color.Yellow);

    public static Style Pending { get; } = Dim;

    public static Style Ready { get; } = Plain;
}
