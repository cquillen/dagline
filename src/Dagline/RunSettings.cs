namespace Dagline;

/// <summary>What one run takes from its options once they have been checked against the graph.</summary>
/// <param name="Sizes">The sizes of the cap and of each limit the graph names.</param>
/// <param name="GracePeriod">
/// How long the run waits for its running tasks after a stop: zero or more, or <see cref="Timeout.InfiniteTimeSpan"/>.
/// </param>
/// <param name="TimeProvider">The clock of the run.</param>
internal sealed record RunSettings(LimitSizes Sizes, TimeSpan GracePeriod, TimeProvider TimeProvider);