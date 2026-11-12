using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// The reporter <see cref="SpectreReporter.Auto"/> gives: it chooses the display or the plain-text reporter when it handles
/// <see cref="RunStarted"/>, and hands that event and every later one of the run to the reporter it chose.
/// </summary>
internal sealed class AutoReporter(TextWriter? plainText, SpectreOptions options) : IReporter
{
    private readonly IAnsiConsole _console = options.Console ?? AnsiConsole.Create(new AnsiConsoleSettings());
    private readonly TimeProvider _clock = options.TimeProvider ?? TimeProvider.System;
    private IReporter? _chosen;

    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (runEvent is RunStarted)
        {
            _chosen = Choose();
        }

        return _chosen?.OnEventAsync(runEvent, snapshot, cancellationToken) ?? ValueTask.CompletedTask;
    }

    private IReporter Choose()
    {
        var writer = plainText ?? Console.Out;
        return UsesDisplay()
            ? new Display(new Terminal(_console), _clock, options.Ascii ? Glyphs.Ascii : Glyphs.Unicode, writer)
            : new PlainTextReporter(writer, _clock);
    }

    private bool UsesDisplay() => options.Display switch
    {
        DisplayMode.Force => true,
        DisplayMode.Never => false,
        _ => !Console.IsOutputRedirected && _console.Profile.Capabilities.Ansi,
    };
}
