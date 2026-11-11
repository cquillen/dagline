namespace Dagline;

/// <summary>
/// What an app chooses for one run. The graph says what the work is, and the options say how this run of it goes.
/// </summary>
/// <remarks>
/// Only <see cref="Journal"/> is required. Every property rejects <see langword="null"/> when it is set.
/// </remarks>
public sealed record RunOptions
{
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
