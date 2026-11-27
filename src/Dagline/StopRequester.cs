namespace Dagline;

/// <summary>Who asked for a stop, as <see cref="StopRequested.By"/> and <see cref="StopState.By"/> give it.</summary>
public enum StopRequester
{
    /// <summary>The app, by cancelling the <see cref="CancellationToken"/> it passed to <see cref="Runner.RunAsync"/>.</summary>
    App,

    /// <summary>Dagline itself, because the run's journal could no longer be written.</summary>
    Dagline,
}
