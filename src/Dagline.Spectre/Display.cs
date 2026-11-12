using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Dagline.Spectre;

/// <summary>
/// The reporter that draws a run on the terminal's alternate screen and leaves the summary on the ordinary screen.
/// It handles the start and end of the run, and draws every other frame on a timer of its own clock.
/// When drawing fails it restores the terminal and hands the rest of the run to a plain-text reporter on
/// <paramref name="plainText"/>.
/// </summary>
internal sealed class Display(Terminal terminal, TimeProvider clock, Glyphs glyphs, TextWriter plainText) : IReporter
{
    private static readonly TimeSpan _redrawInterval = TimeSpan.FromMilliseconds(250);

    // A frame from the timer, an event and the token's callback are never handled at once.
    private readonly Lock _gate = new();
    private ISnapshotSource? _source;
    private ITimer? _timer;
    private bool _onAlternateScreen;
    private (RunStarted Event, Snapshot Snapshot, CancellationToken Token)? _started;
    private CancellationTokenRegistration _abandoned;
    private PlainTextReporter? _plainText;
    private ExceptionDispatchInfo? _timerFailure;
    private HeardEvents _heard = HeardEvents.None;

    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            _timerFailure?.Throw();
            if (_plainText is not null)
            {
                return _plainText.OnEventAsync(runEvent, snapshot, cancellationToken);
            }

            _heard = _heard.After(runEvent);
            try
            {
                Draw(runEvent, snapshot, cancellationToken);
                return ValueTask.CompletedTask;
            }
            catch (Exception failure)
            {
                // The console is the app's and may throw anything. Drawing failed, not the reporter, so the run carries on.
                FallBack(failure);
                return runEvent is RunStarted ? ValueTask.CompletedTask : _plainText!.OnEventAsync(runEvent, snapshot, cancellationToken);
            }
        }
    }

    private void Draw(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        switch (runEvent)
        {
            case RunStarted started:
                Start(started, snapshot, cancellationToken);
                break;
            case RunEnded ended:
                End(ended, snapshot);
                break;
        }
    }

    private void Start(RunStarted started, Snapshot snapshot, CancellationToken cancellationToken)
    {
        _started = (started, snapshot, cancellationToken);
        _abandoned = cancellationToken.Register(static display => ((Display)display!).Abandon(), this);
        _onAlternateScreen = true;
        terminal.EnterAlternateScreen();
        DrawFrame(snapshot);
        _source = started.Source;
        _timer = clock.CreateTimer(_ => DrawTimedFrame(), null, _redrawInterval, _redrawInterval);
    }

    private void End(RunEnded ended, Snapshot snapshot)
    {
        StopTimer();
        _abandoned.Unregister();
        terminal.LeaveAlternateScreen();
        _onAlternateScreen = false;
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
                try
                {
                    DrawFrame(_source.Current);
                }
                catch (Exception failure)
                {
                    // The console is the app's and may throw anything.
                    FallBack(failure);
                }
            }
            catch (Exception failure)
            {
                // The plain-text writer threw too, and an exception that leaves a timer callback ends the process.
                // It is kept for the next event, where the run drops the reporter and records it.
                _timerFailure = ExceptionDispatchInfo.Capture(failure);
            }
        }
    }

    private void DrawFrame(Snapshot snapshot) => terminal.Draw(Frame.Build(snapshot, terminal.Size, clock.GetUtcNow(), glyphs, _heard));

    /// <summary>
    /// Restores the terminal, says on the plain-text writer that the display failed, and hands a plain-text reporter the
    /// <see cref="RunStarted"/> it missed, so that it takes every later event. What the writer or that reporter throws is thrown on.
    /// </summary>
    private void FallBack(Exception failure)
    {
        StopTimer();
        _abandoned.Unregister();
        Restore();
        _plainText = new PlainTextReporter(plainText, clock);
        plainText.WriteLine(FailureLine(failure));
        plainText.Flush();
        if (_started is var (started, snapshot, token))
        {
            // The plain-text reporter writes before it returns, and throws rather than returning a faulted task.
            var handled = _plainText.OnEventAsync(started, snapshot, token);
            Debug.Assert(handled.IsCompletedSuccessfully, "The plain-text reporter handles an event synchronously.");
        }
    }

    private static string FailureLine(Exception failure)
        => $"display failed, output continues as plain text: {failure.GetType().Name}: {failure.Message.ReplaceLineEndings(" ")}";

    /// <summary>Called when a stop ends the wait for reporters while this one is still busy: the terminal is the operator's again.</summary>
    private void Abandon()
    {
        lock (_gate)
        {
            StopTimer();
            Restore();
        }
    }

    private void Restore()
    {
        if (!_onAlternateScreen)
        {
            return;
        }

        _onAlternateScreen = false;
        try
        {
            terminal.LeaveAlternateScreen();
        }
        catch (Exception)
        {
            // The console is the app's and may throw anything. A console that cannot be written to cannot be restored either,
            // and what is still to come must not be lost for it.
        }
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
        _source = null;
    }
}
