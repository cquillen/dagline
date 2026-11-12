using System.Collections.Immutable;

namespace Dagline.Spectre;

/// <summary>
/// What a frame needs that a snapshot does not say, kept from the events the display has handled: the latest warnings,
/// in the order their events came, and who asked for the stop. Immutable: <see cref="After"/> gives the next one.
/// </summary>
/// <param name="LatestWarnings">The latest <see cref="WarningAdded"/> events, the newest first, no more than three.</param>
/// <param name="StopBy">Who asked for the stop, or <see langword="null"/> before <see cref="StopRequested"/> has been handled.</param>
internal sealed record HeardEvents(ImmutableArray<WarningAdded> LatestWarnings, StopRequester? StopBy)
{
    private const int _warningsKept = 3;

    /// <summary>Before any event.</summary>
    public static HeardEvents None { get; } = new([], null);

    /// <summary>What is known once <paramref name="runEvent"/> has been handled as well.</summary>
    public HeardEvents After(RunEvent runEvent) => runEvent switch
    {
        WarningAdded warning => this with { LatestWarnings = [warning, .. LatestWarnings.Take(_warningsKept - 1)] },
        StopRequested stop => this with { StopBy = stop.By },
        _ => this,
    };
}
