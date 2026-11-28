using System.Diagnostics;
using System.Globalization;

namespace Dagline.Tests;

// Throwaway measurements for the eager snapshot take experiment. Not a test of behaviour: each writes one line of numbers to the
// file named by DAGLINE_MEASURE_OUT. The same file is compiled into the baseline and the variant.
public class SnapshotTakeMeasurements
{
    private const int Groups = 200;
    private const int TasksPerGroup = 100;

    [Test]
    [Explicit]
    public void Heap_held_by_the_decided_first_step()
    {
        var graph = ManyReadyAtOnce();
        var options = new RunOptions { Journal = Journal.None };
        var steps = new RunSteps(graph, RunSettings.From(graph, options), earlier: null, new Continuation(graph, earlier: null));

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        var decided = steps.Start(DateTimeOffset.UnixEpoch, drained: false, stopped: false, new UnreadSource());
        clock.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

        var events = decided.Sum(step => step.Events.Count);
        Write(
            $"first-step steps={decided.Count} events={events} starts={decided.Sum(step => step.Starts.Count)} "
            + $"held_bytes={heapAfter - heapBefore} allocated_bytes={allocated} decide_ms={clock.Elapsed.TotalMilliseconds:F1}");

        GC.KeepAlive(decided);
        GC.KeepAlive(steps);
    }

    [Test]
    [Explicit]
    public async Task Peak_heap_of_whole_runs()
    {
        var graph = ManyReadyAtOnce();

        // One run first, so the measured runs are of compiled code.
        await RunOnce(graph);

        for (var run = 1; run <= 7; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var pauseBefore = GC.GetTotalPauseDuration();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

            long peakHeap = 0;
            long peakAfterGc = 0;
            var sampling = true;
            var sampler = new Thread(() =>
            {
                // The memory info is read only once a collection has happened, since reading it allocates.
                var collections = GC.CollectionCount(0);
                while (Volatile.Read(ref sampling))
                {
                    peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(forceFullCollection: false));
                    if (GC.CollectionCount(0) != collections)
                    {
                        collections = GC.CollectionCount(0);
                        peakAfterGc = Math.Max(peakAfterGc, GC.GetGCMemoryInfo(GCKind.Any).HeapSizeBytes);
                    }

                    Thread.Yield();
                }
            })
            { IsBackground = true };
            sampler.Start();

            var clock = Stopwatch.StartNew();
            await RunOnce(graph);
            clock.Stop();

            Volatile.Write(ref sampling, false);
            sampler.Join();

            Write(
                $"whole-run run={run} ms={clock.Elapsed.TotalMilliseconds:F1} "
                + $"allocated_bytes={GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore} "
                + $"peak_heap_over_start={peakHeap - heapBefore} peak_heap_after_a_gc={peakAfterGc} "
                + $"gen0={GC.CollectionCount(0) - gen0} gen1={GC.CollectionCount(1) - gen1} gen2={GC.CollectionCount(2) - gen2} "
                + $"gc_pause_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F1}");
        }

        using var process = Process.GetCurrentProcess();
        Write($"whole-run process_peak_working_set={process.PeakWorkingSet64}");
    }

    private static async Task RunOnce(Graph graph)
    {
        var options = new RunOptions { Journal = Journal.None, Reporters = [new PlainTextReporter(TextWriter.Null)] };
        var result = await Runner.RunAsync(graph, options);
        result.Outcome.ShouldBe(RunOutcome.Complete);
    }

    private static Graph ManyReadyAtOnce()
    {
        var builder = new GraphBuilder();
        for (var index = 1; index <= Groups; index++)
        {
            var group = builder.Group($"group-{index}");
            for (var task = 1; task <= TasksPerGroup; task++)
            {
                group.Task($"task-{task}", static _ => Task.CompletedTask);
            }
        }

        return builder.Build();
    }

    private static void Write(string line)
    {
        var label = Environment.GetEnvironmentVariable("DAGLINE_MEASURE_LABEL") ?? "unlabelled";
        var text = string.Create(CultureInfo.InvariantCulture, $"{label} {line}");
        if (Environment.GetEnvironmentVariable("DAGLINE_MEASURE_OUT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, text + Environment.NewLine);
        }

        Console.WriteLine(text);
    }

    private sealed class UnreadSource : ISnapshotSource
    {
        public Snapshot Current => throw new InvalidOperationException("No reporter reads the run in these measurements.");
    }
}
