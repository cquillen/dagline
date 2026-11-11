namespace Dagline.TestSupport;

/// <summary>
/// A task body that runs until the run is stopped: it waits for the cancellation token on its context, then ends
/// the way the test chose. Use one for one task in one run: <c>builder.Task("load", load.Body)</c>.
/// </summary>
public sealed class StoppedBody
{
    private readonly TaskCompletionSource<TaskContext> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<TaskContext> _onStop;

    /// <summary>Makes a body that ends by throwing the cancellation of its context's token once the run is stopped.</summary>
    public StoppedBody()
        : this(context => context.CancellationToken.ThrowIfCancellationRequested())
    {
    }

    /// <summary>Makes a body that does what <paramref name="onStop"/> does once the run is stopped, then ends.</summary>
    /// <param name="onStop">
    /// Runs once the token is signalled. A body that should succeed in spite of the stop does nothing here,
    /// and one that should fail or be cancelled throws.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="onStop"/> is <see langword="null"/>.</exception>
    public StoppedBody(Action<TaskContext> onStop)
    {
        ArgumentNullException.ThrowIfNull(onStop);
        _onStop = onStop;
    }

    /// <summary>Completes once the run has started the body, with the context the run gave it.</summary>
    public Task<TaskContext> Started => _started.Task;

    /// <summary>The body to give to the builder.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task Body(TaskContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (context.CancellationToken.Register(() => stopped.TrySetResult()))
        {
            _started.TrySetResult(context);
            await stopped.Task;
        }

        _onStop(context);
    }
}
