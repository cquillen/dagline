using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Dagline;

/// <summary>
/// The only way into a run (ADR 0015): many writers, one reader, the run's loop. A post queues the input and returns: it never
/// blocks and runs no engine code. When the loop is waiting for an input, the post hands the loop's continuation to the host,
/// which decides where it runs, and never runs it on the posting thread itself.
/// </summary>
/// <remarks>
/// It is not bounded: a post never waits. Once the loop has gone, the inbox is closed, and what is posted later is let go, but for
/// a checkpoint save, which is refused on the thread that posts it, as is a save still queued when the inbox closes: a task never
/// waits for ever for a save nothing will read.
/// </remarks>
internal sealed class Inbox(IRunHost host)
{
    private readonly ConcurrentQueue<Input> _queue = new();
    private Action? _waiting;
    private volatile bool _closed;

    /// <summary>Whether the loop has gone, so that nothing reads the inbox any more.</summary>
    public bool IsClosed => _closed;

    /// <summary>Queues an input for the loop, and wakes the loop through the host if it is waiting.</summary>
    public void Post(Input input)
    {
        if (_closed)
        {
            LetGo(input);
            return;
        }

        _queue.Enqueue(input);

        // A full fence between queuing and reading whether the inbox is closed, as Close has between closing and letting go, so
        // that an input is never left in a closed inbox.
        Interlocked.MemoryBarrier();
        if (_closed)
        {
            // The loop went between the check and the queuing, so nothing will read this input.
            LetGoOfQueued();
            return;
        }

        Wake();
    }

    /// <summary>
    /// Posts a task's checkpoint save, and gives the task that answers it: once the loop has applied it, or at once, refused, when the
    /// loop has gone. It resumes whoever awaits it as the host's <see cref="IRunHost.CompletionOptions"/> say.
    /// </summary>
    /// <param name="task">The task whose checkpoint is saved.</param>
    /// <param name="text">The checkpoint, or empty to clear it.</param>
    public Task PostSave(BuiltTask task, string text)
    {
        var save = new CheckpointSave(text, host.CompletionOptions);
        Post(Input.SaveCheckpoint(task, save));
        return save.Answered;
    }

    /// <summary>Takes the oldest input, if there is one. For the loop alone.</summary>
    public bool TryTake(out Input input) => _queue.TryDequeue(out input);

    /// <summary>
    /// Awaited by the loop alone, once it has found the inbox empty. It completes when an input is posted, and the loop then goes
    /// on in a turn the host runs.
    /// </summary>
    public InputPosted WaitAsync() => new(this);

    /// <summary>
    /// The loop has gone: from here on nothing reads the inbox, and what was queued and is posted later is let go, a checkpoint save
    /// refused.
    /// </summary>
    public void Close()
    {
        _closed = true;
        Interlocked.MemoryBarrier();
        LetGoOfQueued();
    }

    private void LetGoOfQueued()
    {
        while (_queue.TryDequeue(out var input))
        {
            LetGo(input);
        }
    }

    /// <summary>Lets go of an input nothing will read: a checkpoint save is refused, and anything else is dropped.</summary>
    private static void LetGo(in Input input)
    {
        if (input.Kind is InputKind.CheckpointSave)
        {
            input.Save.RefuseAsTheRunHasEnded(input.Task!);
        }
    }

    private void Wake()
    {
        if (Interlocked.Exchange(ref _waiting, null) is { } continuation)
        {
            host.RunLoop(continuation);
        }
    }

    /// <summary>The awaiter of <see cref="WaitAsync"/>: its continuation is handed to the host, never run inline by a poster.</summary>
    internal readonly struct InputPosted(Inbox inbox) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => !inbox._queue.IsEmpty;

        public InputPosted GetAwaiter() => this;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        // Nothing flows from the poster to the loop: the loop's turn is the host's to run, with no context of the poster's.
        public void UnsafeOnCompleted(Action continuation)
        {
            // A full fence, not a volatile write: the read of the queue below must not move ahead of this store, or a post that
            // finds no continuation and this read that finds no input could miss each other, and nothing would wake the loop.
            Interlocked.Exchange(ref inbox._waiting, continuation);

            // An input posted between the loop finding the inbox empty and the line above found no continuation to hand on.
            if (!inbox._queue.IsEmpty)
            {
                inbox.Wake();
            }
        }
    }
}
