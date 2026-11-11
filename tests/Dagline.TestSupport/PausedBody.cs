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
    public Task Body(TaskContext context)
    {
        _started.TrySetResult(context);
        return _released.Task;
    }

    /// <summary>Lets the body end. The task settles soon after, on another thread: await a signal before asserting on it.</summary>
    public void Release() => _released.TrySetResult();
}
