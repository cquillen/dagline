namespace Dagline.Spectre;

/// <summary>
/// The groups pane: the whole graph in a handful of rows. Each row is a node with its status glyph, its name, a cell for each
/// of its children, and how many of its tasks have succeeded, failed and warned.
/// </summary>
internal static class GroupsPane
{
    /// <summary>
    /// The pane for the run as <paramref name="snapshot"/> shows it: its title, then one row per group two levels down and per
    /// top-level node with no group beneath it, or one row per top-level node when those do not fit in <paramref name="height"/>.
    /// When even those do not fit, the pane keeps to <paramref name="height"/> lines, the last saying how many rows it left out.
    /// A graph with no nodes gives no lines.
    /// </summary>
    /// <param name="snapshot">The run.</param>
    /// <param name="width">The width of the console.</param>
    /// <param name="height">The lines the pane may take, its title included.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    public static IReadOnlyList<StyledLine> Lines(Snapshot snapshot, int width, int height, Glyphs glyphs)
    {
        if (snapshot.Nodes.Count == 0)
        {
            return [];
        }

        var family = new Family(snapshot);
        var rows = snapshot.Nodes.Where(family.HasRowAtTwoLevels).ToList();
        if (rows.Count + 1 > height)
        {
            rows = [.. snapshot.Nodes.Where(node => node.Path.Parent is null)];
        }

        var fits = rows.Count + 1 <= height;
        var shown = fits ? rows : rows.Take(Math.Max(0, height - 2)).ToList();
        var nameWidth = shown.Select(node => Name(node, glyphs).Width).DefaultIfEmpty(0).Max();
        List<StyledLine> lines =
        [
            PaneTitle.Line(StyledLine.Of("Groups", Palette.Bold), width, glyphs),
            .. shown.Select(node => Row(node, family, nameWidth, width, glyphs)),
        ];
        if (!fits)
        {
            lines.Add(TaskLine.More("and " + DisplayText.Count(rows.Count - shown.Count, "more row", "more rows")));
        }

        return lines;
    }

    private static StyledLine Row(NodeSnapshot node, Family family, int nameWidth, int width, Glyphs glyphs)
    {
        var name = Name(node, glyphs);
        var start = Mark(node, family, glyphs).Add(" ").Add(name).Add(new string(' ', nameWidth - name.Width + 1));
        var end = Totals(node, family, glyphs);
        var children = family.ChildrenOf(node);
        if (children.Count == 0)
        {
            return start.Add(end);
        }

        var room = width - start.Width - 1 - end.Width;
        var cells = children.Count <= room
            ? children.Aggregate(StyledLine.Empty, (line, child) => line.Add(Mark(child, family, glyphs)))
            : StatusBar.Of(node.Counts, node.EarlierSuccesses, family.WaitingBeneath(node), Math.Max(0, room), glyphs);
        return start.Add(cells).Add(" ").Add(end);
    }

    private static StyledLine Name(NodeSnapshot node, Glyphs glyphs) => StyledLine.Of(node.Title is { } title ? glyphs.Text(title) : node.Path.Segments[^1]);

    /// <summary>The glyph and colour of a node's status: dimmer for an earlier success, and waiting when all its running tasks are.</summary>
    private static StyledLine Mark(NodeSnapshot node, Family family, Glyphs glyphs)
    {
        if (node.IsEarlierSuccess)
        {
            return StatusMark.EarlierSuccess(glyphs).Line();
        }

        var running = node.Counts[NodeStatus.Running];
        return node.Status is NodeStatus.Running && running > 0 && family.WaitingBeneath(node) == running
            ? StatusMark.Waiting(glyphs).Line()
            : StatusMark.For(node.Status, glyphs).Line();
    }

    /// <summary>Succeeded over total, then the failures and the warnings when there are any, as in <c>1,087/1,104 ✗2 ▲3,173</c>.</summary>
    private static StyledLine Totals(NodeSnapshot node, Family family, Glyphs glyphs)
    {
        var totals = StyledLine.Of(
            $"{DisplayText.Number(node.Counts[NodeStatus.Succeeded])}/{DisplayText.Number(TaskCounts.Total(node.Counts))}");
        var failed = node.Counts[NodeStatus.Failed];
        if (failed > 0)
        {
            var mark = StatusMark.For(NodeStatus.Failed, glyphs);
            totals = totals.Add($" {mark.Glyph}{DisplayText.Number(failed)}", mark.Style);
        }

        var warnings = family.WarningsBeneath(node);
        if (warnings > 0)
        {
            var mark = StatusMark.Warning(glyphs);
            totals = totals.Add($" {mark.Glyph}{DisplayText.Number(warnings)}", mark.Style);
        }

        return totals;
    }

    /// <summary>
    /// What the rows need to know of each node's family, read once from a snapshot: its children, and how many waiting tasks
    /// and warnings are beneath it.
    /// </summary>
    private sealed class Family
    {
        private readonly Dictionary<NodePath, List<NodeSnapshot>> _children = [];
        private readonly Dictionary<NodePath, (int Waiting, int Warnings)> _beneath = [];

        public Family(Snapshot snapshot)
        {
            foreach (var node in snapshot.Nodes)
            {
                _children.Add(node.Path, []);
                if (node.Path.Parent is { } parent)
                {
                    _children[parent].Add(node);
                }

                if (!node.IsGroup)
                {
                    AddUpwards(node);
                }
            }
        }

        public List<NodeSnapshot> ChildrenOf(NodeSnapshot node) => _children[node.Path];

        public int WaitingBeneath(NodeSnapshot node) => _beneath.GetValueOrDefault(node.Path).Waiting;

        public int WarningsBeneath(NodeSnapshot node) => _beneath.GetValueOrDefault(node.Path).Warnings;

        /// <summary>Whether the node has a row when the pane shows the graph two levels deep.</summary>
        public bool HasRowAtTwoLevels(NodeSnapshot node) => IsGroupTwoLevelsDown(node) || IsTopLevelWithNoGroupBeneath(node);

        private static bool IsGroupTwoLevelsDown(NodeSnapshot node) => node.IsGroup && node.Path.Parent is { Parent: null };

        private bool IsTopLevelWithNoGroupBeneath(NodeSnapshot node)
            => node.Path.Parent is null && !ChildrenOf(node).Any(child => child.IsGroup);

        private void AddUpwards(NodeSnapshot task)
        {
            var waiting = Running.IsWaiting(task) ? 1 : 0;
            var warnings = TaskCounts.Warnings(task);
            if (waiting == 0 && warnings == 0)
            {
                return;
            }

            for (NodePath? path = task.Path; path is { } at; path = at.Parent)
            {
                var (beneathWaiting, beneathWarnings) = _beneath.GetValueOrDefault(at);
                _beneath[at] = (beneathWaiting + waiting, beneathWarnings + warnings);
            }
        }
    }
}
