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
    /// The max running tasks: the most tasks that may be running at once in the run, whatever limits they hold. The default is 16.
    /// </summary>
    /// <remarks>
    /// It behaves as a limit that every task holds: a task takes one slot of it when it starts and gives the slot back
    /// when it settles. A ready task that finds it reached stays ready until a task settles.
    /// The default is fixed, not derived from the processor count, since a task mostly waits on other systems.
    /// It must be at least 1: <see cref="Runner.RunAsync"/> refuses a lower one
    /// with <see cref="RunConfigurationException"/> before any task starts.
    /// </remarks>
    public int MaxRunningTasks { get; init; } = 16;

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
    /// <see cref="Snapshot.Stop"/> carries its deadline. It ends early when the last running task ends, and nothing else shortens
    /// it: not a drain, and not the app's token cancelled after a stop that Dagline asked for. When it runs out with tasks still
    /// running, the run publishes <see cref="GraceEnded"/>,
    /// records each of those tasks as <see cref="NodeStatus.Cancelled"/> in declaration order and lets go of it: the run returns
    /// while its body may still be running.
    /// </para>
    /// <para>
    /// <see cref="TimeSpan.Zero"/> lets go of the running tasks at once, in the step that applies the stop.
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits until the running tasks have ended, however long they take. A drain has no
    /// grace period: it waits for the running tasks however long they take, whatever this is.
    /// A limited grace period is at most <see cref="uint.MaxValue"/> - 1 milliseconds, about 49.7 days: the longest timer
    /// a <see cref="System.TimeProvider"/> can create. Any other value below zero, and any value above that limit, is refused by
    /// <see cref="Runner.RunAsync"/> with <see cref="RunConfigurationException"/> before the run exists.
    /// </para>
    /// </remarks>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The journal choice of the run: <see cref="Journal.New"/>, <see cref="Journal.Continue"/>, or <see cref="Journal.None"/>
    /// to run with no journal.
    /// </summary>
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

    /// <summary>
    /// The reporter wait: how long the run waits at its end for its reporters to handle the events still queued for them.
    /// The default is 30 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every run waits for its reporters once, at its end, stopped or not, so that their last lines are printed. The wait is
    /// measured on <see cref="TimeProvider"/> from the moment the event that says the run ended, <see cref="RunEnded"/>, is queued
    /// for the reporters, and it ends early once every reporter has handled every event. A reporter still busy when it runs out
    /// is dropped: the token of its calls is cancelled, the events still queued for it are let go, and the run returns without
    /// waiting for the call in progress, with the reporter on <see cref="RunResult.ReporterErrors"/>, its error an
    /// <see cref="OperationCanceledException"/> naming the event it was handling. A reporter that had handled every event is left
    /// alone, and its token is never cancelled. The run's cancellation token has no part in the wait: cancelling it once the run has
    /// ended does nothing.
    /// </para>
    /// <para>
    /// A run with no reporter still busy at its end makes no timer for the wait. <see cref="TimeSpan.Zero"/> drops a reporter still
    /// busy as soon as the run's end is queued. <see cref="Timeout.InfiniteTimeSpan"/> waits for as long as the reporters take.
    /// On a clock whose timers fire at once, such as the instant clock of <c>Dagline.Testing</c>, a limited wait runs out at once,
    /// so set it to <see cref="Timeout.InfiniteTimeSpan"/> on such a clock to let every reporter finish.
    /// A limited reporter wait is at most <see cref="uint.MaxValue"/> - 1 milliseconds, about 49.7 days: the longest timer a
    /// <see cref="System.TimeProvider"/> can create. Any other value below zero, and any value above that limit, is refused by
    /// <see cref="Runner.Start"/> with <see cref="RunConfigurationException"/> before the run exists, together with every other
    /// problem the options have.
    /// </para>
    /// <para>
    /// If the clock refuses to make the wait's timer, by throwing from <see cref="System.TimeProvider.CreateTimer"/>, the wait
    /// runs out at once: each reporter still busy is dropped, and its error's
    /// <see cref="Exception.InnerException"/> is what the clock threw. The run still returns its result.
    /// </para>
    /// </remarks>
    public TimeSpan ReporterWait { get; init; } = TimeSpan.FromSeconds(30);
}
