namespace Dagline;

/// <summary>
/// All of one run's journal recording: the held file, which entry each change produces, the flush policy, what happens when a
/// write fails, and closing the file. <see cref="Run"/> makes one for each run and hands it each step before publishing the step's
/// events, so that an entry is on disk before any reporter hears of its change (ADR 0010).
/// </summary>
/// <remarks>
/// <para>
/// A run with no journal gets a recorder that records nothing and never fails, so the run never asks whether it has a journal.
/// </para>
/// <para>
/// It is not thread-safe: the run hands it steps and checkpoint changes under its lock, one at a time.
/// </para>
/// </remarks>
internal abstract class JournalRecorder
{
    /// <summary>
    /// Whether a write has failed. From the failed write on nothing more is written, though the file stays held until the run ends.
    /// </summary>
    public abstract Exception? Failure { get; }

    /// <summary>
    /// The recorder of a run on <paramref name="file"/>, or of a run with no journal when it is <see langword="null"/>. It is given
    /// the facts of the run's start here, which the entries of the run's start record.
    /// </summary>
    /// <param name="file">The journal the run is on, already held, opened and read. The recorder owns it from here.</param>
    /// <param name="graph">The graph the run runs.</param>
    /// <param name="settings">The settings of the run: the cap, the limit sizes and the clock that times a checkpoint change.</param>
    /// <param name="continuation">What the run takes from the journal: the set-asides and the counts of journal paths not in the graph.</param>
    public static JournalRecorder For(JournalFile? file, Graph graph, RunSettings settings, Continuation continuation)
        => file is null ? NoJournal.Instance : new OnFile(file, graph, settings, continuation);

    /// <summary>
    /// Records the entries of a decided step, in the order of its events, in one write: the run's start and its set-asides, each
    /// task's start and settle, and the run's end. Every entry but a task's start is flushed, and the entries of a step share one
    /// flush. When the step ends the run, the file is closed and the journal let go of after the write.
    /// </summary>
    public abstract void Record(Step step);

    /// <summary>
    /// Records a task's checkpoint saved, or cleared when <paramref name="text"/> is empty, flushed to the disk before this returns.
    /// It reads the clock once, for the entry. A run with no journal records nothing and reads no clock.
    /// </summary>
    /// <returns>
    /// The time of the entry when it was not written, because this write failed or an earlier one had, so that the run can end its
    /// input with that time and read no clock of its own; otherwise <see langword="null"/>.
    /// </returns>
    public abstract DateTimeOffset? ChangeCheckpoint(BuiltTask task, string text);

    /// <summary>
    /// Whether a write has failed that the run has not yet been told of. It answers <see langword="true"/> once for the failure, so
    /// the run gives its steps the journal write failed input once. A failed write of the run's end is never told, so the run keeps
    /// its outcome.
    /// </summary>
    public abstract bool TakeNewFailure();

    /// <summary>The recorder of a run with no journal: it records nothing and never fails.</summary>
    private sealed class NoJournal : JournalRecorder
    {
        public static readonly NoJournal Instance = new();

        public override Exception? Failure => null;

        public override void Record(Step step)
        {
        }

        public override DateTimeOffset? ChangeCheckpoint(BuiltTask task, string text) => null;

        public override bool TakeNewFailure() => false;
    }

    /// <summary>The recorder of a run on a journal file.</summary>
    private sealed class OnFile : JournalRecorder
    {
        private readonly JournalFile _file;
        private readonly TimeProvider _clock;
        private readonly string _user = Environment.UserName;
        private readonly string _machine = Environment.MachineName;
        private readonly int _cap;
        private readonly Dictionary<string, int> _limits;
        private readonly Continuation _continuation;

        // Reused for each step, so a step allocates no list of its own.
        private readonly List<JournalEntry> _entries = [];

        private Exception? _failure;
        private bool _failureTold;
        private bool _runHasEnded;

        public OnFile(JournalFile file, Graph graph, RunSettings settings, Continuation continuation)
        {
            _file = file;
            _clock = settings.TimeProvider;
            _cap = settings.Sizes.Cap;
            _limits = graph.LimitNames
                .Zip(settings.Sizes.Limits)
                .ToDictionary(limit => limit.First.Text, limit => limit.Second, LimitName.TextComparer);
            _continuation = continuation;
        }

        public override Exception? Failure => _failure;

        public override void Record(Step step)
        {
            // Indexed, not enumerated, so that no enumerator is allocated for each step.
            for (var i = 0; i < step.Events.Count; i++)
            {
                AddEntriesOf(step.Events[i].Event);
            }

            if (_entries.Count > 0)
            {
                // Every entry but a start is flushed, and entries written together share one flush, so one is enough for the step.
                var flush = _entries.Exists(static entry => entry is not TaskStartedEntry) ? JournalFlush.ToDisk : JournalFlush.None;
                TryWrite(_entries, flush);
                _entries.Clear();
            }

            if (step.Ends is not null)
            {
                _runHasEnded = true;
                _file.Dispose();
            }
        }

        public override DateTimeOffset? ChangeCheckpoint(BuiltTask task, string text)
        {
            var path = task.Path.ToString();
            var now = _clock.GetUtcNow();
            JournalEntry entry = text.Length == 0 ? new CheckpointClearedEntry(path, now) : new CheckpointSavedEntry(path, now, text);
            return TryWrite([entry], JournalFlush.ToDisk) ? null : now;
        }

        public override bool TakeNewFailure()
        {
            // The one exception to a failed write stopping the run is the last write of a run, its end, and once the run has ended
            // no other write is made. By then every task's work is done and its settle is on disk, and the run outcome is decided
            // with nothing left to stop. Failing the run would tell the app that a finished run failed, so the run is never told of
            // that failure and keeps its outcome, and only the journal shows the failure: it records no end.
            if (_failure is null || _failureTold || _runHasEnded)
            {
                return false;
            }

            _failureTold = true;
            return true;
        }

        /// <summary>The entries a change records, if any. Becoming ready, a report, a group and a stop are never recorded.</summary>
        private void AddEntriesOf(RunEvent change)
        {
            switch (change)
            {
                case RunStarted started:
                    _entries.Add(new RunStartedEntry(
                        started.At,
                        _user,
                        _machine,
                        _cap,
                        _limits,
                        _continuation.PathsNotInGraph,
                        _continuation.PathsNotInGraphUnsucceeded));

                    // A run decides its set-asides before it starts, and flushes them with its start, before any task starts.
                    foreach (var task in _continuation.SetAside)
                    {
                        _entries.Add(new SetAsideEntry(task.Path.ToString(), started.At));
                    }

                    break;
                case TaskStatusChanged { To: NodeStatus.Running } moved:
                    _entries.Add(new TaskStartedEntry(moved.Node.Path.ToString(), moved.At));
                    break;
                case TaskStatusChanged moved when moved.To.IsSettled():
                    _entries.Add(TaskSettledEntry.Of(moved.Node, moved.At));
                    break;
                case RunEnded { Outcome: { } outcome } ended:
                    _entries.Add(new RunEndedEntry(ended.At, outcome));
                    break;
            }
        }

        /// <summary>
        /// Writes entries to the file, unless a write has already failed: this is the one guard on writing nothing after a failure.
        /// When the write fails, the failure is kept and nothing more is written, though the file stays held until the run ends.
        /// </summary>
        /// <returns><see langword="false"/> when this write failed or an earlier one had.</returns>
        private bool TryWrite(IReadOnlyList<JournalEntry> entries, JournalFlush flush)
        {
            if (_failure is not null)
            {
                return false;
            }

            try
            {
                _file.Append(entries, flush);
                return true;
            }
            catch (Exception cause) when (FileSystemFailure.Is(cause))
            {
                _failure = cause;
                return false;
            }
        }
    }
}
