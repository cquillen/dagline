using System.Globalization;

namespace Dagline;

/// <summary>A reporter that writes a run as lines of text.</summary>
public sealed class PlainTextReporter : IReporter
{
    private readonly TextWriter _writer;

    /// <summary>Makes a reporter that writes to <paramref name="writer"/>.</summary>
    /// <param name="writer">Where the lines go.</param>
    /// <param name="timeProvider">The clock.</param>
    public PlainTextReporter(TextWriter writer, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <inheritdoc />
    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        switch (runEvent)
        {
            case RunStarted:
                Line(runEvent, $"run started: {CountsText.Tasks(snapshot.Counts.Values.Sum())}");
                break;

            case TaskStatusChanged { To: NodeStatus.Running } changed:
                Line(runEvent, $"started {changed.Node.Path}");
                break;

            case TaskStatusChanged { To: NodeStatus.Succeeded } changed:
                Line(runEvent, $"succeeded {changed.Node.Path} in {DurationText.Of(changed.At - changed.Node.Started!.Value)}");
                break;

            case RunEnded ended:
                Line(runEvent, $"run ended: {ended.Outcome.ToString().ToLowerInvariant()} in {DurationText.Of(ended.At - snapshot.Started)}");
                _writer.WriteLine($"tasks: {CountsText.PerStatus(snapshot.Counts)}");
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void Line(RunEvent runEvent, string text)
        => _writer.WriteLine($"{runEvent.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)} {text}");
}
