namespace Dagline;

/// <summary>
/// What a run gives a task body: the task's identity, the run's clock, and the means to report on the task.
/// </summary>
/// <remarks>
/// A run creates one context for each task it starts, so a graph that is run twice gives each run's bodies their own contexts.
/// A context is safe to use from several threads at once.
/// </remarks>
public sealed class TaskContext
{
    private readonly Run _run;
    private readonly GraphTask _task;

    internal TaskContext(Run run, GraphTask task, TimeProvider timeProvider)
    {
        _run = run;
        _task = task;
        TimeProvider = timeProvider;
    }

    /// <summary>The path of the task this body is running as.</summary>
    public NodePath Path => _task.Path;

    /// <summary>
    /// The clock of the run, as given in <see cref="RunOptions.TimeProvider"/>.
    /// Take any delay or timestamp of the task's own from it, so that a test of the task controls time.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Sets the status text of the task's progress: one line that says what the task is doing now.</summary>
    /// <param name="text">The new status text, which replaces the last one. <see langword="null"/> clears it.</param>
    public void Status(string? text) => _run.Report(_task, task => task.WithStatusText(text));

    /// <summary>Sets the count of the task's progress, with or without the total it is counting towards.</summary>
    /// <param name="current">How far the task has got. It replaces the last count.</param>
    /// <param name="total">
    /// What the count is counting towards, or <see langword="null"/> for a running tally. It replaces the last total,
    /// so a call without one clears it.
    /// </param>
    public void Count(long current, long? total = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        if (total is { } towards)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(towards, nameof(total));
        }

        _run.Report(_task, task => task.WithCount(current, total));
    }

    /// <summary>Adds a warning to the task's outcome.</summary>
    /// <param name="warning">The warning. Each line break in it is replaced by a space.</param>
    /// <exception cref="ArgumentNullException"><paramref name="warning"/> is <see langword="null"/>.</exception>
    public void Warn(string warning)
    {
        ArgumentNullException.ThrowIfNull(warning);

        _run.Report(_task, task => task.WithWarning(warning));
    }

    /// <summary>Sets the message of the task's outcome.</summary>
    /// <param name="message">The new message, which replaces the last one. Each line break in it is replaced by a space.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public void Message(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _run.Report(_task, task => task.WithMessage(message));
    }

    /// <summary>Adds detail lines to the task's outcome.</summary>
    /// <param name="lines">The lines to add after those already there. Text that holds line breaks is split into one line for each.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A line in <paramref name="lines"/> is <see langword="null"/>. Nothing is added.</exception>
    public void Detail(params string[] lines)
    {
        ThrowIfAnyNull(lines, nameof(lines));

        _run.Report(_task, task => task.WithDetail(lines));
    }

    private static void ThrowIfAnyNull(string[] lines, string paramName)
    {
        ArgumentNullException.ThrowIfNull(lines, paramName);

        if (lines.Contains(null))
        {
            throw new ArgumentException("A line is null.", paramName);
        }
    }
}
