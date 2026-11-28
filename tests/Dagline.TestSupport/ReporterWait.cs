namespace Dagline.TestSupport;

/// <summary>
/// Sets the reporter wait of a run, which stays an internal setting until #154 makes it a
/// run option. It goes then, and tests set the option itself.
/// </summary>
public static class ReporterWait
{
    /// <summary>
    /// The same options, with the reporter wait <paramref name="wait"/>: how long a run waits for its reporters
    /// once its end is queued, on the run's clock, or <see cref="Timeout.InfiniteTimeSpan"/> to wait for as long as they take.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static RunOptions WithReporterWait(this RunOptions options, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with { ReporterWait = wait };
    }
}
