using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.TestSupport;

/// <summary>Helpers for tests that control time with <see cref="FakeTimeProvider"/>.</summary>
public static class FakeClock
{
    private static readonly TimeSpan _stuckAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Advances the clock one <paramref name="step"/> at a time until <paramref name="signal"/> completes,
    /// letting other threads run between steps.
    /// </summary>
    /// <param name="clock">The clock the run was given.</param>
    /// <param name="signal">A task the test owns that completes when what the test waits for has happened, such as the run itself.</param>
    /// <param name="step">
    /// How far to advance each time, which is more than zero. Pick one no longer than the shortest delay the test cares about.
    /// </param>
    /// <returns>A task that completes once <paramref name="signal"/> has completed, whether it succeeded or not. Await the signal to see which.</returns>
    /// <remarks>
    /// The fake clock cannot say when a timer is pending, so a test cannot advance it by exactly the right amount at the right moment.
    /// The clock may therefore end up further on than the delay needed: assert on what happened, or on a least duration, not on an exact time.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> or <paramref name="signal"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="step"/> is zero or negative. A clock that does not move would never complete a signal that waits on it,
    /// and the test would only fail on its timeout.
    /// </exception>
    /// <exception cref="TimeoutException">The signal did not complete within 30 seconds of real time: the test is stuck.</exception>
    public static async Task AdvanceUntilAsync(this FakeTimeProvider clock, Task signal, TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);

        var waited = Stopwatch.StartNew();
        while (!signal.IsCompleted)
        {
            if (waited.Elapsed > _stuckAfter)
            {
                throw new TimeoutException($"The signal did not complete while the clock was advanced to {clock.GetUtcNow():O}.");
            }

            clock.Advance(step);
            await Task.Yield();
        }
    }
}
