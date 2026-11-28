namespace Dagline.TestSupport;

/// <summary>Runs a graph to its end on the new engine on the thread pool, as an app would, until the new engine replaces the existing one.</summary>
public static class NewEngine
{
    /// <summary>Runs <paramref name="graph"/> on the new engine, with its loop, its bodies and its reporters on the thread pool.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static Task<RunResult> RunAsync(Graph graph, RunOptions options) => LoopRun.Start(graph, options, ThreadPoolHost.Instance).Result;
}
