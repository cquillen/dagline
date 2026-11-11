namespace Dagline.TestSupport;

/// <summary>
/// A task body, shared by several tasks of one run, that counts how many of them are running at once
/// and remembers the highest number seen. A test of the cap or of a limit gives it to every task the bound covers
/// and asserts on <see cref="Highest"/> once the run has ended.
/// </summary>
/// <remarks>
/// Every body stays in flight until <paramref name="expected"/> of them are running at once, so the bound is always reached
/// and never by luck. A run that lets fewer of them run at once never ends, and the test fails on its timeout.
/// </remarks>
/// <param name="expected">How many of the bodies the run is expected to let run at once: the size of the bound under test.</param>
public sealed class OverlappingBodies(int expected)
{
    private readonly TaskCompletionSource _overlapped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _running;
    private int _highest;

    /// <summary>The highest number of these bodies that were running at once, so far.</summary>
    public int Highest => Volatile.Read(ref _highest);

    /// <summary>The body to give to the builder, for every task the bound under test covers.</summary>
    public async Task Body(TaskContext context)
    {
        var running = Interlocked.Increment(ref _running);
        RecordHighest(running);
        if (running >= expected)
        {
            _overlapped.TrySetResult();
        }

        await _overlapped.Task;

        // Stay in flight a little longer, so that a body the run should not have started has the chance to be counted.
        await Task.Yield();
        await Task.Yield();
        Interlocked.Decrement(ref _running);
    }

    private void RecordHighest(int running)
    {
        var highest = Volatile.Read(ref _highest);
        while (running > highest)
        {
            var seen = Interlocked.CompareExchange(ref _highest, running, highest);
            if (seen == highest)
            {
                return;
            }

            highest = seen;
        }
    }
}
