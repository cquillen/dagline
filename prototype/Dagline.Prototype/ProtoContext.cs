using System.Text;

namespace Dagline.Prototype;

internal delegate Task ProtoBody(ProtoContext context);

/// <summary>
/// What the prototype gives a task body: the stand-in for Dagline's TaskContext, which is welded to the engine being replaced.
/// Every member either posts an input or overwrites the task's slot. None reads the run's state, and none waits, except a save.
/// </summary>
internal sealed class ProtoContext
{
    private const int _checkpointBytes = 4096;

    private readonly Inbox _inbox;
    private readonly BuiltTask _task;
    private readonly TaskSlot _slot;
    private readonly CheckpointCell _checkpoint;
    private readonly TaskCreationOptions _completionOptions;

    public ProtoContext(
        Inbox inbox,
        BuiltTask task,
        TaskSlot slot,
        string checkpoint,
        TimeProvider timeProvider,
        TaskCreationOptions completionOptions,
        CancellationToken cancellationToken)
    {
        _inbox = inbox;
        _task = task;
        _slot = slot;
        _checkpoint = new CheckpointCell(checkpoint);
        _completionOptions = completionOptions;
        TimeProvider = timeProvider;
        CancellationToken = cancellationToken;
    }

    public NodePath Path => _task.Path;

    public TimeProvider TimeProvider { get; }

    public CancellationToken CancellationToken { get; }

    // Progress: a sample. Nothing is posted.
    public void SetStatusText(string? text) => _slot.SetStatusText(text);

    public void SetCount(long current, long? total = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        _slot.SetCount(current, total);
    }

    // A warning that is kept is an input; one beyond those kept is counted in the slot.
    public void AddWarning(string warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        if (_slot.TryKeepWarning())
        {
            _inbox.Post(Input.Warning(_task, warning));
        }
    }

    public void SetMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _inbox.Post(Input.Message(_task, message));
    }

    public void AddDetail(params string[] lines)
    {
        if (Kept(lines) is { Length: > 0 } kept)
        {
            _inbox.Post(Input.Detail(_task, kept));
        }
    }

    public ReportedFailureException Fail(string message, params string[] detail)
    {
        ArgumentNullException.ThrowIfNull(message);
        _inbox.Post(Input.Failure(_task, message, Kept(detail)));

        return new ReportedFailureException(LineBreaks.OneLine(message), [.. detail.SelectMany(LineBreaks.Lines)]);
    }

    /// <summary>
    /// Opens a waiting scope. Unlike Dagline's, it cannot say whether the task is still running, since that is the run's state:
    /// it always posts, and the core ignores an open or a close on a task that is not running.
    /// </summary>
    public IDisposable BeginWaiting(string? text = null)
    {
        _inbox.Post(Input.WaitOpened(_task, text));
        return new Scope(_inbox, _task);
    }

    /// <summary>The task's checkpoint as the task last saw it: what it started with, or the last save that was answered.</summary>
    public string Checkpoint => _checkpoint.Text;

    /// <summary>Completes once the loop has the entry on disk. With no journal that is still a trip through the inbox.</summary>
    public Task SaveCheckpointAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > _checkpointBytes)
        {
            throw new ArgumentException($"A checkpoint is at most {_checkpointBytes} bytes as UTF-8.", nameof(text));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var completion = new SaveCompletion(_checkpoint, _completionOptions);
        _inbox.Post(Input.CheckpointSave(_task, text, completion));
        return completion.Task;
    }

    /// <summary>A copy of Dagline's helper, ConfigureAwait(false) included, so the manual host is tested against the real shape.</summary>
    public async Task WaitUntilAsync(TimeSpan interval, Func<CancellationToken, Task<bool>> check, string? text = null)
    {
        CancellationToken.ThrowIfCancellationRequested();

        using (BeginWaiting(text))
        {
            while (!await check(CancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(interval, TimeProvider, CancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The same helper without ConfigureAwait(false). In an app a body starts on the thread pool with no context, so this changes
    /// nothing there; under a manual host it keeps the helper's continuations where the host can see them.
    /// </summary>
    public async Task WaitUntilOnTheCallersContextAsync(TimeSpan interval, Func<CancellationToken, Task<bool>> check, string? text = null)
    {
        CancellationToken.ThrowIfCancellationRequested();

        using (BeginWaiting(text))
        {
            while (!await check(CancellationToken))
            {
                await Task.Delay(interval, TimeProvider, CancellationToken);
            }
        }
    }

    // The task's side splits the lines and decides which are kept, since those beyond are counted here and never posted.
    private string[] Kept(string[] texts)
    {
        string[] lines = [.. texts.SelectMany(LineBreaks.Lines)];
        var kept = _slot.KeepDetail(lines.Length);
        return kept == lines.Length ? lines : lines[..kept];
    }

    private sealed class Scope(Inbox inbox, BuiltTask task) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inbox.Post(Input.WaitClosed(task));
            }
        }
    }
}

/// <summary>
/// Pairs prototype bodies with a real Dagline graph. The graph is built with Dagline's own GraphBuilder, whose bodies take a
/// TaskContext the prototype cannot make; so each task is given a placeholder body, and this remembers which prototype body the
/// placeholder stands for.
/// </summary>
internal sealed class ProtoBodies
{
    private readonly Dictionary<TaskBody, ProtoBody> _bodies = new(ReferenceEqualityComparer.Instance);

    /// <summary>The placeholder to give GraphBuilder for <paramref name="body"/>.</summary>
    public TaskBody Of(ProtoBody body)
    {
        // Captures the body, so that each call makes a delegate of its own: one that captured nothing would be shared.
        TaskBody placeholder = _ => throw new InvalidOperationException($"A placeholder body: the prototype runs {body.Method.Name}, which it stands for.");
        _bodies.Add(placeholder, body);
        return placeholder;
    }

    public ProtoBody BodyOf(BuiltTask task) => _bodies[task.Body];
}
