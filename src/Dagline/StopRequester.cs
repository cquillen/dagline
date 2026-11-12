namespace Dagline;

/// <summary>Who asked for a stop, as <see cref="StopRequested.By"/> gives it.</summary>
public enum StopRequester
{
    /// <summary>The app, through the <see cref="StopSignal"/> it gave the run.</summary>
    App,

    /// <summary>Dagline itself, because the run's journal could no longer be written.</summary>
    Dagline,
}
