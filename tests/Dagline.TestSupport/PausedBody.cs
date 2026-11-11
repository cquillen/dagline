namespace Dagline.TestSupport;

/// <summary>
/// A task body that pauses until the test releases it, so that a test can catch a run mid-flight without sleeping.
/// Use one for one task in one run: <c>builder.Task("prepare", prepare.Body)</c>.
/// </summary>
public sealed class PausedBody
{
    private readonly TaskCompletionSource<TaskContext> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the run has started the body, with the context the run gave it.
    /// The task is running from then until <see cref="Release"/>.
    /// </summary>
    public Task<TaskContext> Started => _started.Task;

    /// <summary>The body to give to the builder. It ends, and its task succeeds, once the test calls <see cref="Release"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task Body(TaskContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _started.TrySetResult(context);
        return _released.Task;
    }

    /// <summary>Lets the body end. The task settles soon after, on another thread: await a signal before asserting on it.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>
    /// Lets the body end by throwing <paramref name="exception"/>. The body ignores its cancellation token until then,
    /// so a test can keep it running after a stop.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public void Throw(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _released.TrySetException(exception);
    }
}
