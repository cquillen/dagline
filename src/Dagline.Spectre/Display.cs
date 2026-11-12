using System.Runtime.ExceptionServices;

namespace Dagline.Spectre;

/// <summary>
/// The reporter that draws a run on the terminal's alternate screen and leaves the summary on the ordinary screen.
/// It handles the start and end of the run, and draws every other frame on a timer of its own clock.
/// </summary>
internal sealed class Display(Terminal terminal, TimeProvider clock, Glyphs glyphs) : IReporter
{
    private static readonly TimeSpan _redrawInterval = TimeSpan.FromMilliseconds(250);

    // A frame from the timer and an event are never drawn at once.
    private readonly Lock _gate = new();
    private ISnapshotSource? _source;
    private ITimer? _timer;
    private ExceptionDispatchInfo? _timerFailure;

    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            _timerFailure?.Throw();
            switch (runEvent)
            {
                case RunStarted started:
                    Start(started.Source, snapshot);
                    break;
                case RunEnded ended:
                    End(ended, snapshot);
                    break;
            }
        }

        return ValueTask.CompletedTask;
    }

    private void Start(ISnapshotSource source, Snapshot snapshot)
    {
        StopTimer();
        terminal.EnterAlternateScreen();
        DrawFrame(snapshot);
        _source = source;
        _timer = clock.CreateTimer(_ => DrawTimedFrame(), null, _redrawInterval, _redrawInterval);
    }

    private void End(RunEnded ended, Snapshot snapshot)
    {
        StopTimer();
        terminal.LeaveAlternateScreen();
        terminal.Print(Summary.Lines(ended, snapshot, terminal.Size.Width, glyphs));
    }

    /// <summary>Called by the timer, on a thread of its own.</summary>
    private void DrawTimedFrame()
    {
        lock (_gate)
        {
            if (_source is null)
            {
                return;
            }

            try
            {
                DrawFrame(_source.Current);
            }
            catch (Exception failure)
            {
                // The console is the app's and may throw anything, and an exception that leaves a timer callback ends the
                // process. It is kept for the next event, where the run can record it.
                _timerFailure = ExceptionDispatchInfo.Capture(failure);
                StopTimer();
            }
        }
    }

    private void DrawFrame(Snapshot snapshot) => terminal.Draw(Frame.Build(snapshot, terminal.Size, clock.GetUtcNow(), glyphs));

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
        _source = null;
    }
}
