using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Dagline.Prototype;

/// <summary>
/// The only way into a run: many writers, one reader. A post enqueues and returns; it never blocks and runs no engine code.
/// When the loop is waiting for an input, the post hands the loop's continuation to the host, which decides where it runs.
/// </summary>
internal sealed class Inbox(IRunHost host)
{
    private readonly ConcurrentQueue<Input> _queue = new();
    private Action? _waiter;
    private volatile bool _closed;
    private int _count;

    /// <summary>The most inputs that were ever queued at once: nothing bounds it, since a post never waits.</summary>
    public int Peak { get; private set; }

    public int Posted { get; private set; }

    /// <summary>How many times a post found the loop waiting and asked the host to run it.</summary>
    public int Wakes { get; private set; }

    public bool IsEmpty => _queue.IsEmpty;

    public void Post(in Input input)
    {
        if (_closed)
        {
            Late(input);
            return;
        }

        _queue.Enqueue(input);
        var count = Interlocked.Increment(ref _count);
        if (count > Peak)
        {
            // Racy on purpose: a statistic for the findings, not state.
            Peak = count;
        }

        Posted++;
        if (_closed)
        {
            // The loop left between the check and the enqueue, so nobody will read this: let it go here.
            LetGoOfQueued();
            return;
        }

        Wake();
    }

    public bool TryRead(out Input input)
    {
        if (_queue.TryDequeue(out input))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }

        return false;
    }

    /// <summary>Awaited by the loop alone. Its continuation is run by the host, never inline by the thread that posts.</summary>
    public Waiting WaitAsync() => new(this);

    /// <summary>
    /// The loop has returned: from here on nothing reads the inbox. A save posted later is still answered, on the thread that
    /// posts it, because a task must never wait for ever on a save.
    /// </summary>
    public void Close()
    {
        _closed = true;
        LetGoOfQueued();
    }

    private void LetGoOfQueued()
    {
        while (TryRead(out var input))
        {
            Late(input);
        }
    }

    private static void Late(in Input input)
    {
        if (input.Kind is InputKind.CheckpointSave)
        {
            ((SaveCompletion)input.Payload!).Refused(SaveRefusals.NotRunning(input.Task!));
        }
    }

    private void Wake()
    {
        if (Interlocked.Exchange(ref _waiter, null) is { } waiter)
        {
            Wakes++;
            host.RunLoop(waiter);
        }
    }

    internal readonly struct Waiting(Inbox inbox) : INotifyCompletion
    {
        public Waiting GetAwaiter() => this;

        public bool IsCompleted => !inbox._queue.IsEmpty;

        public void OnCompleted(Action continuation)
        {
            Volatile.Write(ref inbox._waiter, continuation);

            // An input posted between IsCompleted and the line above found no waiter, so nothing woke the loop for it.
            if (!inbox._queue.IsEmpty)
            {
                inbox.Wake();
            }
        }

        public void GetResult()
        {
        }
    }
}

internal static class SaveRefusals
{
    public static InvalidOperationException NotRunning(BuiltTask task) => new(
        $"The task '{task.Path}' cannot change its checkpoint: it has settled, or the run has let go of it, "
        + "so the run no longer keeps anything it does. A task changes its checkpoint only while it is running.");
}
