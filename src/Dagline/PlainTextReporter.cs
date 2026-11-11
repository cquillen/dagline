using System.Runtime.ExceptionServices;

namespace Dagline;

/// <summary>
/// A reporter that prints a run as timestamped lines of text, one line per thing that happened, and a summary at the end.
/// Its output reads as a log when it is redirected to a file or shown by a build server.
/// </summary>
/// <remarks>
/// <para>
/// It prints a line when the run starts, when a task starts, when a task succeeds or fails (with its duration and its message),
/// when a task adds a warning, when a group settles (with its count of tasks per status), and when the run ends.
/// A failed task's detail lines follow its line. A task that becomes ready is not printed, and neither is progress.
/// A path is printed and a title is not.
/// </para>
/// <para>
/// Consecutive events of tasks blocked by the same failed task are printed as one line, such as <c>blocked 2 tasks by checks/b</c>.
/// The reporter cannot tell that the last of them has arrived until something else happens, so the line is printed
/// just before the next line of any other kind: at the next event that is not one of them, or with the line of counts below.
/// It carries the time of the events, not the time it was printed.
/// </para>
/// <para>
/// One minute after the last line it printed, while the run has not ended, the reporter prints one line of counts,
/// such as <c>tasks: 1 running, 2 succeeded</c>, read from the snapshot source of the run, so that a quiet hour does not
/// look like a hang. The minute is measured on the reporter's own clock, and that line carries the time of that clock.
/// </para>
/// <para>
/// After the line for the end of the run comes the summary, with no timestamps: the count of tasks per status,
/// then every failed task (the kind of failure, when it failed, how long it ran, the progress it had reported, its message,
/// its detail lines and what it blocked, named by the roots of <see cref="BlockedTasks"/>), then every warning that was kept,
/// under its task. A section with nothing in it is left out, so a complete run with no warnings ends with the counts alone.
/// </para>
/// <para>
/// Every line about an event begins with the time of the event in UTC, as <c>2026-11-11T04:12:03Z</c>. Numbers are written
/// in the invariant culture with no grouping. A duration is <c>3.1s</c> under a minute, <c>1m15s</c> under an hour and
/// <c>2h14m</c> from there, rounded down. A list of counts names each status that has at least one task, in lifecycle order,
/// or reads <c>no tasks</c>. Where Dagline kept only the first 100 detail lines or warnings of a task, a line such as
/// <c>and 3 more</c> follows them.
/// </para>
/// <para>
/// The reporter writes to the writer it was given and to nothing else. All the lines about one event, the summary included,
/// are written with one call to <see cref="TextWriter.Write(string)"/>, each line ended by the writer's
/// <see cref="TextWriter.NewLine"/>, and followed by a call to <see cref="TextWriter.Flush"/>. Its own writes never overlap:
/// the line of counts, which is written from a timer, waits for a write that is in progress. The writes are synchronous.
/// </para>
/// <para>
/// When the writer throws while an event is handled, the exception leaves <see cref="OnEventAsync"/>, so the run drops
/// the reporter and lists the error in <see cref="RunResult.ReporterErrors"/>, and no further line of counts is printed.
/// When the writer throws on a line of counts, no further line of counts is printed either, and the exception is thrown
/// from the next call to <see cref="OnEventAsync"/>, so that it reaches <see cref="RunResult.ReporterErrors"/> the same way.
/// </para>
/// <para>
/// Give a reporter to one run at a time. It may be given to another run once the first has ended.
/// </para>
/// </remarks>
public sealed class PlainTextReporter : IReporter
{
    private static readonly TimeSpan _idleAfter = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly TextWriter _writer;
    private readonly TimeProvider _timeProvider;
    private BlockedRun? _blocked;
    private ISnapshotSource? _source;
    private ITimer? _idleTimer;
    private long _lastLine;
    private ExceptionDispatchInfo? _idleLineFailure;

    /// <summary>Makes a reporter that prints to a writer the app owns.</summary>
    /// <param name="writer">
    /// Where every line goes, such as standard output or a file. The reporter never closes it.
    /// </param>
    /// <param name="timeProvider">
    /// The clock that measures the quiet minute before a line of counts and gives that line its time.
    /// The default is <see cref="TimeProvider.System"/>. It drives nothing else: the time on every other line is the time
    /// of its event, from the clock of the run. A test passes the same fake clock as it gives the run.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public PlainTextReporter(TextWriter writer, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Prints the lines about one event, if it is one that is printed.</summary>
    /// <param name="runEvent">The event.</param>
    /// <param name="snapshot">The run as of the event. The summary is taken from the one handed with <see cref="RunEnded"/>.</param>
    /// <param name="cancellationToken">Not used: a write is synchronous and is not abandoned halfway.</param>
    /// <returns>A task that has already completed: the lines are written before the method returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runEvent"/> or <paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Whatever the writer throws is thrown on from here, and so is what it threw on an earlier line of counts.
    /// </remarks>
    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            try
            {
                Handle(runEvent, snapshot);
            }
            catch (Exception)
            {
                // The writer is the app's code and may throw anything. The run drops a reporter that throws,
                // so nothing would ever stop the line of counts after this.
                StopIdleLine();
                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    private void Handle(RunEvent runEvent, Snapshot snapshot)
    {
        if (runEvent is RunStarted started)
        {
            StartOver(started.Source);
        }

        _idleLineFailure?.Throw();

        var blockedBy = runEvent is TaskStatusChanged { To: NodeStatus.Blocked } blocked ? blocked.Node.BlockedBy : null;
        if (blockedBy is { } sameCause && _blocked?.Cause == sameCause)
        {
            _blocked = _blocked.Value with { Tasks = _blocked.Value.Tasks + 1 };
            return;
        }

        var blockedLine = TakeBlockedLine();
        if (blockedBy is { } cause)
        {
            _blocked = new BlockedRun(runEvent.At, cause, Tasks: 1);
        }

        Print([.. blockedLine, .. PlainTextLines.About(runEvent, snapshot)]);

        if (runEvent is RunEnded)
        {
            StopIdleLine();
        }
    }

    /// <summary>Forgets whatever an earlier run left behind and starts counting the quiet time of this one.</summary>
    private void StartOver(ISnapshotSource source)
    {
        StopIdleLine();
        _blocked = null;
        _idleLineFailure = null;
        _source = source;

        // Not due yet: the first line printed sets it going.
        _idleTimer = _timeProvider.CreateTimer(_ => PrintIdleLine(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The line for the blocked events counted so far, which are counted no further. Empty when there are none.</summary>
    private string[] TakeBlockedLine()
    {
        if (_blocked is not { } run)
        {
            return [];
        }

        _blocked = null;
        return [PlainTextLines.Blocked(run.At, run.Tasks, run.Cause)];
    }

    private void Print(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // One call for all the lines of one event, so that a writer the app also writes to cannot put a line of its own among them.
        _writer.Write(string.Concat(lines.Select(line => line + _writer.NewLine)));
        _writer.Flush();

        _lastLine = _timeProvider.GetTimestamp();
        _idleTimer?.Change(_idleAfter, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Called by the timer, on a thread of its own, when a minute may have passed since the last line.</summary>
    private void PrintIdleLine()
    {
        lock (_gate)
        {
            if (_idleTimer is null || _source is null)
            {
                return;
            }

            // The timer can fire while a line about an event is being printed, and a timer may fire a little early.
            var quiet = _timeProvider.GetElapsedTime(_lastLine);
            if (quiet < _idleAfter)
            {
                _idleTimer.Change(_idleAfter - quiet, Timeout.InfiniteTimeSpan);
                return;
            }

            try
            {
                Print([.. TakeBlockedLine(), PlainTextLines.Idle(_timeProvider.GetUtcNow(), _source.Current)]);
            }
            catch (Exception failure)
            {
                // The writer is the app's code and may throw anything, and an exception that leaves a timer callback
                // ends the process. It is kept for the next event, where the run can record it.
                _idleLineFailure = ExceptionDispatchInfo.Capture(failure);
                StopIdleLine();
            }
        }
    }

    private void StopIdleLine()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    /// <summary>Consecutive blocked events with one cause, which are printed as one line.</summary>
    private readonly record struct BlockedRun(DateTimeOffset At, NodePath Cause, int Tasks);
}
