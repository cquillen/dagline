using System.Collections.Frozen;

namespace Dagline;

/// <summary>
/// What one failed task blocked: an entry of <see cref="Snapshot.Blocked"/>.
/// </summary>
/// <remarks>
/// A summary names the <paramref name="Roots"/> and counts the <paramref name="Tasks"/>, so a failure that blocked
/// a thousand tasks beneath one group reads as that one group.
/// </remarks>
/// <param name="Cause">
/// The path of the failed task. Every task in <paramref name="Tasks"/> has it as its <see cref="NodeSnapshot.BlockedBy"/>.
/// </param>
/// <param name="Tasks">
/// Every task this failure blocked, directly, through a group or through a chain of blocked tasks, in declaration order. Never empty.
/// </param>
/// <param name="Roots">
/// The highest nodes whose every task this failure blocked, in declaration order. A root is a group when the failure blocked
/// every task beneath it at any depth and did not block every task beneath the group that holds it. Otherwise it is
/// a blocked task itself. Every task in <paramref name="Tasks"/> is one root or beneath exactly one, and a group with no tasks
/// beneath it is never a root.
/// </param>
public sealed record BlockedTasks(NodePath Cause, IReadOnlyList<NodeSnapshot> Tasks, IReadOnlyList<NodeSnapshot> Roots)
{
    /// <summary>
    /// Every task this failure blocked, directly, through a group or through a chain of blocked tasks, in declaration order.
    /// Never empty in an entry of <see cref="Snapshot.Blocked"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value given is <see langword="null"/>.</exception>
    public IReadOnlyList<NodeSnapshot> Tasks { get; init => field = Arguments.NotNull(value); } = Arguments.NotNull(Tasks);

    /// <summary>
    /// The highest nodes whose every task this failure blocked, in declaration order. A root is a group when the failure blocked
    /// every task beneath it at any depth and did not block every task beneath the group that holds it. Otherwise it is
    /// a blocked task itself. Every task in <see cref="Tasks"/> is one root or beneath exactly one, and a group with no tasks
    /// beneath it is never a root.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value given is <see langword="null"/>.</exception>
    public IReadOnlyList<NodeSnapshot> Roots { get; init => field = Arguments.NotNull(value); } = Arguments.NotNull(Roots);

    /// <summary>One entry for each failed task that blocked anything among <paramref name="nodes"/>, in declaration order of the cause.</summary>
    /// <param name="nodes">Every node of a run at one instant, in declaration order.</param>
    /// <param name="positions">Where each node is in declaration order.</param>
    internal static IReadOnlyList<BlockedTasks> Among(IReadOnlyList<NodeSnapshot> nodes, FrozenDictionary<NodePath, int> positions)
    {
        return
        [
            .. nodes
                .Where(node => node.BlockedBy is not null)
                .GroupBy(task => task.BlockedBy!.Value)
                .OrderBy(blocked => positions[blocked.Key])
                .Select(blocked => ByOneCause(blocked.Key, [.. blocked])),
        ];

        BlockedTasks ByOneCause(NodePath cause, IReadOnlyList<NodeSnapshot> tasks) => new(cause, tasks, Roots(tasks));

        IReadOnlyList<NodeSnapshot> Roots(IReadOnlyList<NodeSnapshot> tasks)
        {
            var blockedBeneath = tasks.SelectMany(task => GroupsThatHold(task.Path)).CountBy(group => group);
            var wholeGroups = blockedBeneath.Where(group => group.Value == TasksBeneath(group.Key)).Select(group => group.Key);
            HashSet<NodePath> whollyBlocked = [.. tasks.Select(task => task.Path), .. wholeGroups];

            return
            [
                .. whollyBlocked
                    .Where(path => path.Parent is not { } parent || !whollyBlocked.Contains(parent))
                    .Select(path => positions[path])
                    .Order()
                    .Select(position => nodes[position]),
            ];
        }

        int TasksBeneath(NodePath group) => nodes[positions[group]].Counts.Total;
    }

    private static IEnumerable<NodePath> GroupsThatHold(NodePath path)
    {
        for (var group = path.Parent; group is not null; group = group.Value.Parent)
        {
            yield return group.Value;
        }
    }
}
