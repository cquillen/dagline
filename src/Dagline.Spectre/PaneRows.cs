namespace Dagline.Spectre;

/// <summary>How many lines each of the panes below the groups takes in a frame, and the one rule that shares them out.</summary>
/// <param name="Working">The lines of the working pane.</param>
/// <param name="Waiting">The lines of the waiting pane.</param>
/// <param name="Problems">The lines of the problems pane.</param>
internal readonly record struct PaneRows(int Working, int Waiting, int Problems)
{
    /// <summary>The lines a pane is given before any pane grows: its title and one more, which lists a task or says how many it left out.</summary>
    public const int Least = 2;

    /// <summary>
    /// Shares <paramref name="lines"/> between the panes, none getting more than it <paramref name="wanted"/>. First each pane
    /// with something to show gets <see cref="Least"/> lines, working first; then problems grow to a third of the lines,
    /// waiting to all it wants (the pane itself lists at most five tasks), and working to all it wants. Problems never take more
    /// than a third, or <see cref="Least"/> when a third is fewer, so lines that no pane can use are left blank.
    /// </summary>
    /// <param name="lines">The lines left after the header, the banner and the groups.</param>
    /// <param name="wanted">How many lines each pane would take if it had all the lines it could use.</param>
    public static PaneRows Share(int lines, PaneRows wanted)
    {
        var free = Math.Max(0, lines);
        var third = free / 3;
        var working = Grow(0, Math.Min(wanted.Working, Least));
        var waiting = Grow(0, Math.Min(wanted.Waiting, Least));
        var problems = Grow(0, Math.Min(wanted.Problems, Least));
        problems = Grow(problems, Math.Min(wanted.Problems, third));
        waiting = Grow(waiting, wanted.Waiting);
        working = Grow(working, wanted.Working);
        return new(working, waiting, problems);

        int Grow(int given, int target)
        {
            var more = Math.Clamp(target - given, 0, free);
            free -= more;
            return given + more;
        }
    }
}
