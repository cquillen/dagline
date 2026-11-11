namespace Dagline;

/// <summary>
/// One waiting scope of a task, which closes when disposed. Disposing it again does nothing.
/// A scope opened when the task was not running has no run to tell and does nothing at all.
/// </summary>
internal sealed class WaitingScope : IDisposable
{
    private readonly Run? _run;
    private readonly BuiltTask _task;
    private int _closed;

    public WaitingScope(Run? run, BuiltTask task)
    {
        _run = run;
        _task = task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _run?.CloseWaitingScope(_task);
        }
    }
}
