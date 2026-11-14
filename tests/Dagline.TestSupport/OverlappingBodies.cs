namespace Dagline.TestSupport;

/// <summary>
/// A task body, shared by several tasks of one run, that counts how many of them are running at once
/// and remembers the highest number seen. A test of the max running tasks or of a limit gives it to every task the bound covers
/// and asserts on <see cref="Highest"/> once the run has ended.
/// </summary>
/// <remarks>
/// <para>
/// Every body stays in flight until the expected number of them are running at once, so the bound is always reached
/// and never by luck. A run that lets fewer of them run at once never ends, and the test fails on its timeout.
/// </para>
/// <para>
/// It counts a body once the body has a thread, which is later than the moment the run started the task. A body the run
/// should not have started can therefore begin after the others have left and not be counted, so <see cref="Highest"/>
/// shows that a bound was reached but cannot alone show that it was never exceeded. A test asserts as well on the snapshots
/// a recording reporter was handed: the highest number of tasks running, and the highest number of slots in use,
/// across them. The snapshot handed with the start of a task counts that task whenever its body runs.
/// </para>
/// </remarks>
public sealed class OverlappingBodies
{
    private readonly TaskCompletionSource _overlapped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _expected;
    private int _running;
    private int _highest;

    /// <summary>Makes the body for the tasks of one run.</summary>
    /// <param name="expected">
    /// How many of the bodies the run is expected to let run at once: the size of the bound under test, which is at least 1.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expected"/> is zero or negative.</exception>
    public OverlappingBodies(int expected)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expected);
        _expected = expected;
    }

    /// <summary>The highest number of these bodies that were running at once, so far.</summary>
    public int Highest => Volatile.Read(ref _highest);

    /// <summary>The body to give to the builder, for every task the bound under test covers.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task Body(TaskContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var running = Interlocked.Increment(ref _running);
        RecordHighest(running);
        if (running >= _expected)
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
