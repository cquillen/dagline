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

        var tree = new Tree(snapshot);
        var rows = snapshot.Nodes.Where(tree.IsTwoLevelRow).ToList();
        if (rows.Count + 1 > height)
        {
            rows = [.. snapshot.Nodes.Where(node => node.Path.Parent is null)];
        }

        var nameWidth = rows.Max(node => Name(node).Width);
        return [PaneTitle.Line(StyledLine.Of("Groups", Palette.Bold), width, glyphs), .. rows.Select(node => Row(node, tree, nameWidth, width, glyphs))];
    }

    private static StyledLine Row(NodeSnapshot node, Tree tree, int nameWidth, int width, Glyphs glyphs)
    {
        var name = Name(node);
        var start = Mark(node, tree, glyphs).Add(" ").Add(name).Add(new string(' ', nameWidth - name.Width + 1));
        var end = Totals(node, tree, glyphs);
        var children = tree.ChildrenOf(node);
        if (children.Count == 0)
        {
            return start.Add(end);
        }

        var room = width - start.Width - 1 - end.Width;
        var cells = children.Count <= room
            ? children.Aggregate(StyledLine.Empty, (line, child) => line.Add(Mark(child, tree, glyphs)))
            : StatusBar.Of(node.Counts, node.EarlierSuccesses, tree.WaitingBeneath(node), Math.Max(0, room), glyphs);
        return start.Add(cells).Add(" ").Add(end);
    }

    private static StyledLine Name(NodeSnapshot node) => StyledLine.Of(node.Title ?? node.Path.Segments[^1]);

    /// <summary>The glyph and colour of a node's status: dimmer for an earlier success, and waiting when all its running tasks are.</summary>
    private static StyledLine Mark(NodeSnapshot node, Tree tree, Glyphs glyphs)
    {
        if (node.IsEarlierSuccess)
        {
            return StyledLine.Of(glyphs.Succeeded, Palette.EarlierSuccess);
        }

        var running = node.Counts[NodeStatus.Running];
        return node.Status is NodeStatus.Running && running > 0 && tree.WaitingBeneath(node) == running
            ? StyledLine.Of(glyphs.Waiting, Palette.Waiting)
            : StyledLine.Of(glyphs.For(node.Status), Palette.For(node.Status));
    }

    /// <summary>Succeeded over total, then the failures and the warnings when there are any, as in <c>1,087/1,104 ✗2 ▲3,173</c>.</summary>
    private static StyledLine Totals(NodeSnapshot node, Tree tree, Glyphs glyphs)
    {
        var totals = StyledLine.Of(
            $"{DisplayText.Number(node.Counts[NodeStatus.Succeeded])}/{DisplayText.Number(node.Counts.Values.Sum())}");
        var failed = node.Counts[NodeStatus.Failed];
        if (failed > 0)
        {
            totals = totals.Add($" {glyphs.Failed}{DisplayText.Number(failed)}", Palette.Failed);
        }

        var warnings = tree.WarningsBeneath(node);
        if (warnings > 0)
        {
            totals = totals.Add($" {glyphs.Warning}{DisplayText.Number(warnings)}", Palette.Warning);
        }

        return totals;
    }

    /// <summary>The children of every node, and the waiting tasks and warnings beneath it, read once from a snapshot.</summary>
    private sealed class Tree
    {
        private readonly Dictionary<NodePath, List<NodeSnapshot>> _children = [];
        private readonly Dictionary<NodePath, (int Waiting, int Warnings)> _beneath = [];

        public Tree(Snapshot snapshot)
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

        /// <summary>A group two levels down, or a top-level node with no group beneath it.</summary>
        public bool IsTwoLevelRow(NodeSnapshot node) => node.Path.Parent switch
        {
            null => !ChildrenOf(node).Any(child => child.IsGroup),
            { } parent => node.IsGroup && parent.Parent is null,
        };

        private void AddUpwards(NodeSnapshot task)
        {
            var waiting = task.Status is NodeStatus.Running && task.WaitingSince is not null ? 1 : 0;
            var warnings = task.Warnings.Count + task.WarningsOmitted;
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
