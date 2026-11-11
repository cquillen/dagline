using System.Collections.ObjectModel;

namespace Dagline;

/// <summary>
/// What an app chooses for one run. The graph says what the work is, and the options say how this run of it goes.
/// </summary>
/// <remarks>
/// Only <see cref="Journal"/> is required. Every property that could hold <see langword="null"/> rejects it when it is set.
/// </remarks>
public sealed record RunOptions
{
    /// <summary>
    /// The cap: the most tasks that may be running at once in the run, whatever limits they hold. The default is 16.
    /// </summary>
    /// <remarks>
    /// The cap behaves as a limit that every task holds: a task takes one slot of it when it starts and gives the slot back
    /// when it settles. A ready task that finds the cap reached stays ready until a task settles.
    /// The default is fixed, not derived from the processor count, since a task mostly waits on other systems.
    /// The cap must be at least 1: <see cref="Runner.RunAsync"/> refuses a lower one
    /// with <see cref="RunConfigurationException"/> before any task starts.
    /// </remarks>
    public int Cap { get; init; } = 16;

    /// <summary>
    /// The size of each limit, by name: how many of the tasks that hold the limit may be running at once.
    /// The default is no sizes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The graph names limits, on tasks and on groups, and the run sizes them here. Every limit the graph names needs a size,
    /// and every size must be at least 1: <see cref="Runner.RunAsync"/> refuses anything else
    /// with <see cref="RunConfigurationException"/> before any task starts.
    /// A size for a limit the graph does not name is ignored, so one set of sizes can serve several graphs.
    /// </para>
    /// <para>
    /// The sizes are read once when the run starts. Names are compared ordinally, whatever comparer the dictionary was made with.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public IReadOnlyDictionary<string, int> Limits
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }
    = ReadOnlyDictionary<string, int>.Empty;

    /// <summary>
    /// The clock of the run. Event times, each task's start and settle times, and the run's own start and end are read from it,
    /// and every task body receives it on its context. The default is <see cref="TimeProvider.System"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public TimeProvider TimeProvider
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }
    = TimeProvider.System;

    /// <summary>
    /// The grace period: how long the run waits, once a stop has been applied, for its running tasks to end.
    /// The default is 30 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wait is measured on <see cref="TimeProvider"/> from the moment the stop is applied, and
    /// <see cref="Snapshot.Stop"/> carries its deadline. It ends early when the last running task ends, and at once on a second
    /// request on the <see cref="StopSignal"/>. When it ends with tasks still running, the run publishes <see cref="StoppedWaiting"/>,
    /// records each of those tasks as <see cref="NodeStatus.Cancelled"/> in declaration order and lets go of it: the run returns
    /// while its body may still be running.
    /// </para>
    /// <para>
    /// <see cref="TimeSpan.Zero"/> lets go of the running tasks at once, in the step that applies the stop.
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits until the running tasks have ended, or until a second request.
    /// A limited grace period is at most <see cref="uint.MaxValue"/> - 1 milliseconds, about 49.7 days: the longest timer
    /// a <see cref="System.TimeProvider"/> can create. Any other value below zero, and any value above that limit, is refused by
    /// <see cref="Runner.RunAsync"/> with <see cref="RunConfigurationException"/> before the run exists.
    /// </para>
    /// </remarks>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The journal choice of the run. <see cref="Journal.None"/> runs with no journal.</summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public required Journal Journal
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>
    /// The reporters that watch the run. Each receives every event of the run in order.
    /// The list is read once when the run starts, and no reporter joins later. The default is no reporters.
    /// </summary>
    /// <remarks>
    /// Each reporter has a queue of its own, so the run never waits for one while tasks are running and a slow reporter
    /// does not hold up another. One that throws is dropped and is on <see cref="RunResult.ReporterErrors"/>.
    /// <see cref="IReporter.OnEventAsync"/> says what a reporter may rely on.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public IReadOnlyList<IReporter> Reporters
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }
    = [];
}
