namespace Dagline.TestSupport;

/// <summary>
/// Sets the reporter wait of a run on the new engine, which stays an internal setting until the cutover of spec #139 makes it a
/// run option. It goes then, and tests set the option itself.
/// </summary>
public static class ReporterWait
{
    /// <summary>
    /// The same options, with the reporter wait <paramref name="wait"/>: how long a run on the new engine waits for its reporters
    /// once its end is queued, on the run's clock, or <see cref="Timeout.InfiniteTimeSpan"/> to wait for as long as they take.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static RunOptions WithReporterWait(this RunOptions options, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with { ReporterWait = wait };
    }
}
