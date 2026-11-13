using System.Collections.Immutable;

namespace Dagline.Spectre;

/// <summary>
/// What a frame needs that a snapshot does not say, kept from the events the display has handled: the latest warnings,
/// in the order their events came, since a snapshot does not say when a warning was added. Immutable: <see cref="After"/>
/// gives the next one.
/// </summary>
/// <param name="LatestWarnings">The latest <see cref="WarningAdded"/> events, the newest first, no more than three.</param>
internal sealed record HeardEvents(ImmutableArray<WarningAdded> LatestWarnings)
{
    private const int _warningsKept = 3;

    /// <summary>Before any event.</summary>
    public static HeardEvents None { get; } = new([]);

    /// <summary>What is known once <paramref name="runEvent"/> has been handled as well.</summary>
    public HeardEvents After(RunEvent runEvent) => runEvent switch
    {
        WarningAdded warning => this with { LatestWarnings = [warning, .. LatestWarnings.Take(_warningsKept - 1)] },
        _ => this,
    };
}
