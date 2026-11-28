namespace Dagline;

/// <summary>
/// The checkpoint of each task of one run, as its context reads it back, by the task's position among the graph's nodes. The loop
/// alone writes a task's, as it answers the task's save, before the answer, so a save that has completed reads back its text; any
/// thread may read it at any time.
/// </summary>
internal sealed class TaskCheckpoints
{
    private readonly string?[] _texts;

    /// <param name="graph">The graph the run runs.</param>
    /// <param name="atTheStart">The checkpoint each task begins the run with: what a continued journal hands back, or empty.</param>
    public TaskCheckpoints(Graph graph, Func<BuiltTask, string> atTheStart)
    {
        _texts = new string?[graph.Nodes.Length];
        foreach (var task in graph.Tasks)
        {
            _texts[task.Position] = atTheStart(task);
        }
    }

    /// <summary>The checkpoint of <paramref name="task"/> as of the last save answered: empty when there is none.</summary>
    public string this[BuiltTask task] => Volatile.Read(ref _texts[task.Position])!;

    /// <summary>The save of <paramref name="task"/> is made: from now on it reads back <paramref name="text"/>. For the loop alone.</summary>
    public void Saved(BuiltTask task, string text) => Volatile.Write(ref _texts[task.Position], text);
}
