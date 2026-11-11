// PROTOTYPE (throwaway, stubs only). Graph building.
using System.Collections;

namespace Dagline;

/// <summary>Identity of a node: segments from the root joined by '/'. Value type, validated on creation.</summary>
public readonly record struct NodePath : IComparable<NodePath>
{
    public static NodePath Parse(string text) => throw new NotImplementedException();
    public IReadOnlyList<string> Segments => throw new NotImplementedException();
    public NodePath Parent => throw new NotImplementedException();
    public int CompareTo(NodePath other) => throw new NotImplementedException();
    public override string ToString() => throw new NotImplementedException();
}

/// <summary>A task body: async delegate over its context. Throwing is an unexpected failure.</summary>
public delegate Task TaskBody(TaskContext context);

/// <summary>Handle to a node, returned when the node is created. Dependencies are declared on handles only, so a typo does not compile.</summary>
public abstract class Node
{
    public NodePath Path => throw new NotImplementedException();
}

public sealed class TaskNode : Node
{
    public TaskNode Title(string title) => throw new NotImplementedException();
    /// <summary>Holds a slot of the named limit while running.</summary>
    public TaskNode Limit(string name) => throw new NotImplementedException();
    public TaskNode Requires(Node upstream) => throw new NotImplementedException();
    public TaskNode Requires(IEnumerable<Node> upstreams) => throw new NotImplementedException();
    public TaskNode After(Node upstream) => throw new NotImplementedException();
    public TaskNode After(IEnumerable<Node> upstreams) => throw new NotImplementedException();
}

/// <summary>A group handle and, at once, the scope in which children are added.</summary>
public sealed class GroupNode : Node
{
    public GroupNode Group(string segment) => throw new NotImplementedException();
    public TaskNode Task(string segment, TaskBody body) => throw new NotImplementedException();
    public GroupNode Title(string title) => throw new NotImplementedException();
    /// <summary>Every task beneath the group holds a slot of the named limit.</summary>
    public GroupNode Limit(string name) => throw new NotImplementedException();
    public GroupNode Requires(Node upstream) => throw new NotImplementedException();
    public GroupNode Requires(IEnumerable<Node> upstreams) => throw new NotImplementedException();
    public GroupNode After(Node upstream) => throw new NotImplementedException();
    public GroupNode After(IEnumerable<Node> upstreams) => throw new NotImplementedException();
}

/// <summary>Collects nodes in declaration order. Build validates everything and throws one GraphException.</summary>
public sealed class GraphBuilder
{
    public GroupNode Group(string segment) => throw new NotImplementedException();
    public TaskNode Task(string segment, TaskBody body) => throw new NotImplementedException();
    public Graph Build() => throw new NotImplementedException();
}

/// <summary>An immutable, validated graph. Enumerates nodes in declaration order.</summary>
public sealed class Graph : IReadOnlyList<Node>
{
    public int Count => throw new NotImplementedException();
    public Node this[int index] => throw new NotImplementedException();
    public IEnumerator<Node> GetEnumerator() => throw new NotImplementedException();
    IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
}
