using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

/// <summary>
/// Shadows <see cref="Dagline.Runner"/> for this project's tests, so that each run they start goes through the engine switch and
/// CI's second run puts it on the new engine (spec #139). It goes at the cutover, with the switch.
/// </summary>
internal static class Runner
{
    /// <summary>Runs <paramref name="graph"/> as <see cref="Dagline.Runner.RunAsync"/> does, on the engine the switch chooses.</summary>
    public static Task<RunResult> RunAsync(
        Graph graph,
        RunOptions options,
        DrainSignal? drain = null,
        CancellationToken cancellationToken = default)
        => EngineSwitch.RunAsync(graph, options, drain, cancellationToken);
}
