namespace Dagline.TestSupport;

/// <summary>
/// Where every run a test project starts goes, so that CI's second run of the suite runs it on the new engine (spec #139), until
/// the cutover deletes the existing engine, this switch and that run.
/// </summary>
/// <remarks>
/// <para>
/// The switch is on for a whole test process when the environment variable <see cref="Variable"/> is <c>new</c>, which only CI's
/// second run sets, and no test writes. It is read at each run, so the switch keeps no state of its own, and what it turns on is
/// carried by the options of that one run: no test can leak it to another.
/// </para>
/// <para>
/// Each test project's own <c>Runner</c> forwards here: it shadows <see cref="Dagline.Runner"/> in the project's namespace, so the
/// tests' calls reach the switch unchanged. <c>tests/second-run.txt</c> lists the test classes the second run includes.
/// </para>
/// </remarks>
public static class EngineSwitch
{
    /// <summary>The environment variable that turns the switch on for the process when it is <c>new</c>.</summary>
    public const string Variable = "DAGLINE_TEST_ENGINE";

    /// <summary>
    /// Runs <paramref name="graph"/> through <see cref="Runner.RunAsync"/>: on the new engine when the switch is on, as
    /// <see cref="NewEngine.RunAsync"/> does, and on the existing one otherwise.
    /// </summary>
    public static Task<RunResult> RunAsync(
        Graph graph,
        RunOptions options,
        DrainSignal? drain = null,
        CancellationToken cancellationToken = default)
        => IsOn && options is not null
            ? NewEngine.RunAsync(graph, options, drain, cancellationToken)
            : Runner.RunAsync(graph, options!, drain, cancellationToken);

    private static bool IsOn => Environment.GetEnvironmentVariable(Variable) == "new";
}
