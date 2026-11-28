namespace Dagline;

/// <summary>
/// The <see cref="TaskSample"/> of each task of one run, by the task's position among the graph's nodes. Made with the run's step
/// core, and handed from there to the tasks' contexts, which write them, and to the run's current snapshot, which reads them.
/// </summary>
internal sealed class TaskSamples
{
    private readonly TaskSample?[] _samples;

    public TaskSamples(Graph graph)
    {
        _samples = new TaskSample?[graph.Nodes.Length];
        foreach (var task in graph.Tasks)
        {
            _samples[task.Position] = new TaskSample();
        }
    }

    /// <summary>The sample of the task at <paramref name="position"/>, which must be a task's.</summary>
    public TaskSample this[int position] => _samples[position]!;

    /// <summary>The sample of <paramref name="task"/>.</summary>
    public TaskSample this[BuiltTask task] => _samples[task.Position]!;
}
