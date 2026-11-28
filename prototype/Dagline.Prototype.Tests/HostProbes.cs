using System.IO.Pipes;
using System.Text;
using Dagline.Testing;

namespace Dagline.Prototype.Tests;

/// <summary>
/// Question A, measured: for each way a task body can await, under each way the manual host can start bodies, does every piece of
/// the body run on the pumping thread, and does "pump until idle" finish the run?
/// </summary>
internal static class HostProbes
{
    public sealed record Observation(
        string Body,
        BodyMode Mode,
        bool AmbientContextAtDrive,
        bool EveryPieceOnThePumpingThread,
        bool IdlePumpFinishedTheRun,
        bool PumpUntilFinishedTheRun,
        string Outcome);

    public static readonly string[] Bodies =
    [
        "tcs", "tcs-RunContinuationsAsynchronously", "tcs-ConfigureAwait(false)",
        "fake-delay", "fake-delay-ConfigureAwait(false)",
        "stop-cancels-fake-delay", "stop-cancels-fake-delay-ConfigureAwait(false)", "stop-cancels-tcs-ConfigureAwait(false)",
        "yield", "ConfigureAwait(false)-then-yield",
        "save", "save-ConfigureAwait(false)",
        "instant-clock-delay", "task-run", "file-read", "pipe-read",
    ];

    public static Observation Probe(string body, BodyMode mode, bool ambientContextAtDrive = false)
    {
        var scenario = new Scenario(mode);
        var pumping = Environment.CurrentManagedThreadId;
        var offThread = 0;
        var gate = new Gate(body.StartsWith("tcs-Run", StringComparison.Ordinal) ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None);
        var instant = new InstantClock();
        var file = Path.Combine(Path.GetTempPath(), $"dagline-prototype-probe-{Guid.NewGuid():N}.bin");
        var pipeName = $"dagline-prototype-probe-{Guid.NewGuid():N}";
        NamedPipeServerStream? server = null;
        NamedPipeClientStream? client = null;
        Action drive = () => { };

        // Called by the body after each await. A piece that runs off the pumping thread takes real time, as real work would, so
        // that a pump that returns "idle" while it is still going is seen to have returned early.
        void Mark()
        {
            if (Environment.CurrentManagedThreadId != pumping)
            {
                Interlocked.Increment(ref offThread);
                Thread.Sleep(150);
            }
        }

        ProtoBody probe = body switch
        {
            "tcs" or "tcs-RunContinuationsAsynchronously" => async _ =>
            {
                await gate.Task;
                Mark();
            },
            "tcs-ConfigureAwait(false)" => async _ =>
            {
                await gate.Task.ConfigureAwait(false);
                Mark();
            },
            "fake-delay" => async context =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), context.TimeProvider, context.CancellationToken);
                Mark();
            },
            "fake-delay-ConfigureAwait(false)" => async context =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), context.TimeProvider, context.CancellationToken).ConfigureAwait(false);
                Mark();
            },
            "stop-cancels-fake-delay" => async context =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken);
                }
                finally
                {
                    Mark();
                }
            },
            "stop-cancels-fake-delay-ConfigureAwait(false)" => async context =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Mark();
                }
            },
            "stop-cancels-tcs-ConfigureAwait(false)" => async context =>
            {
                // Not a delay: a source of the body's own, cancelled from a callback it registered on the run's token.
                var cancelled = new TaskCompletionSource();
                using var registration = context.CancellationToken.Register(() => cancelled.TrySetCanceled());
                try
                {
                    await cancelled.Task.ConfigureAwait(false);
                }
                finally
                {
                    Mark();
                }
            },
            "yield" => async _ =>
            {
                await Task.Yield();
                Mark();
            },
            "ConfigureAwait(false)-then-yield" => async _ =>
            {
                await gate.Task.ConfigureAwait(false);
                Mark();
                await Task.Yield();
                Mark();
            },
            "save" => async context =>
            {
                await context.SaveCheckpointAsync("job-17");
                Mark();
            },
            "save-ConfigureAwait(false)" => async context =>
            {
                await context.SaveCheckpointAsync("job-17").ConfigureAwait(false);
                Mark();
            },
            "instant-clock-delay" => async context =>
            {
                await Task.Delay(TimeSpan.FromHours(1), instant, context.CancellationToken);
                Mark();
            },
            "task-run" => async _ =>
            {
                await Task.Run(() => Thread.Sleep(20));
                Mark();
            },
            "file-read" => async _ =>
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                var buffer = new byte[1 << 20];
                while (await stream.ReadAsync(buffer) > 0)
                {
                    Mark();
                }
            },
            "pipe-read" => async _ =>
            {
                var buffer = new byte[1];
                await server!.ReadAsync(buffer);
                Mark();
            },
            _ => throw new ArgumentOutOfRangeException(nameof(body)),
        };

        switch (body)
        {
            case "tcs" or "tcs-RunContinuationsAsynchronously" or "tcs-ConfigureAwait(false)" or "ConfigureAwait(false)-then-yield":
                drive = gate.Open;
                break;
            case "fake-delay" or "fake-delay-ConfigureAwait(false)":
                drive = () => scenario.Clock.Advance(TimeSpan.FromSeconds(10));
                break;
            case "stop-cancels-fake-delay" or "stop-cancels-fake-delay-ConfigureAwait(false)" or "stop-cancels-tcs-ConfigureAwait(false)":
                drive = () =>
                {
                    scenario.Run.Stop();
                    scenario.Host.PumpLoop();
                };
                break;
            case "file-read":
                File.WriteAllBytes(file, new byte[8 << 20]);
                break;
            case "pipe-read":
                server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                client.Connect();
                server.WaitForConnection();
                drive = () =>
                {
                    client.WriteByte(1);
                    client.Flush();
                };
                break;
        }

        try
        {
            scenario.Task("probe", probe);
            var run = scenario.Start(gracePeriod: Timeout.InfiniteTimeSpan);
            scenario.Host.PumpUntilIdle();

            if (ambientContextAtDrive)
            {
                // As when the test's own thread has a context of its own: a UI thread, or a test framework that installs one.
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new AmbientContext());
                try
                {
                    drive();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }
            else
            {
                drive();
            }

            scenario.Host.PumpUntilIdle();
            var idleFinished = run.Result.IsCompleted;
            var finished = scenario.Host.PumpUntil(() => run.Result.IsCompleted, TimeSpan.FromSeconds(3));
            return new Observation(
                body,
                mode,
                ambientContextAtDrive,
                Volatile.Read(ref offThread) == 0,
                idleFinished,
                finished,
                finished ? ProtoOutcome.Name(run.Result.Result.Outcome) : "did not end");
        }
        finally
        {
            client?.Dispose();
            server?.Dispose();
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    public static string Table(IEnumerable<Observation> observations)
    {
        var table = new StringBuilder();
        table.AppendLine("| body awaits | bodies started by | context current at the drive | every piece on the pumping thread | pump-until-idle finished the run | pump-until(condition) finished the run |");
        table.AppendLine("|---|---|---|---|---|---|");
        foreach (var seen in observations)
        {
            table.AppendLine(
                $"| {seen.Body} | {seen.Mode} | {(seen.AmbientContextAtDrive ? "a foreign one" : "none")} | {YesNo(seen.EveryPieceOnThePumpingThread)} "
                + $"| {YesNo(seen.IdlePumpFinishedTheRun)} | {YesNo(seen.PumpUntilFinishedTheRun)} ({seen.Outcome}) |");
        }

        return table.ToString();

        static string YesNo(bool value) => value ? "yes" : "NO";
    }

    private sealed class AmbientContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ThreadPool.QueueUserWorkItem(_ => d(state));
    }
}

public class HostProbeTests
{
    /// <summary>Runs every probe and writes what it saw beside the test binaries. FINDINGS.md quotes the table.</summary>
    [Test]
    [NotInParallel]
    public void Write_the_matrix()
    {
        List<HostProbes.Observation> seen = [];
        foreach (var body in HostProbes.Bodies)
        {
            foreach (var mode in new[] { BodyMode.SynchronizationContext, BodyMode.TaskScheduler, BodyMode.ThreadPool })
            {
                seen.Add(HostProbes.Probe(body, mode));
            }
        }

        foreach (var body in new[] { "tcs", "tcs-ConfigureAwait(false)", "fake-delay", "fake-delay-ConfigureAwait(false)" })
        {
            seen.Add(HostProbes.Probe(body, BodyMode.SynchronizationContext, ambientContextAtDrive: true));
            seen.Add(HostProbes.Probe(body, BodyMode.TaskScheduler, ambientContextAtDrive: true));
        }

        var path = Path.Combine(AppContext.BaseDirectory, "probe-matrix.md");
        File.WriteAllText(
            path,
            $"ambient SynchronizationContext of a TUnit test thread: {SynchronizationContext.Current?.GetType().FullName ?? "none"}\n\n"
            + HostProbes.Table(seen));
    }

    private static readonly BodyMode[] _onTheHost = [BodyMode.SynchronizationContext, BodyMode.TaskScheduler];

    [Test]
    public void Bodies_that_await_test_primitives_the_runs_clock_a_yield_or_a_save_stay_on_the_pumping_thread()
    {
        string[] bodies =
        [
            "tcs", "tcs-RunContinuationsAsynchronously", "tcs-ConfigureAwait(false)", "fake-delay", "fake-delay-ConfigureAwait(false)",
            "stop-cancels-fake-delay", "yield", "save", "save-ConfigureAwait(false)",
        ];
        foreach (var mode in _onTheHost)
        {
            foreach (var body in bodies)
            {
                var seen = HostProbes.Probe(body, mode);
                seen.EveryPieceOnThePumpingThread.ShouldBeTrue($"{body} under {mode}");
                seen.IdlePumpFinishedTheRun.ShouldBeTrue($"{body} under {mode}");
            }
        }
    }

    [Test]
    public void ConfigureAwait_false_leaves_the_pumping_thread_in_three_ways()
    {
        foreach (var mode in _onTheHost)
        {
            // 1. A cancelled Task.Delay never resumes its awaiter inline: the runtime sends it to the thread pool.
            Left(HostProbes.Probe("stop-cancels-fake-delay-ConfigureAwait(false)", mode));

            // 2. Once a continuation has run with no context, the next yield goes to the thread pool.
            Left(HostProbes.Probe("ConfigureAwait(false)-then-yield", mode));

            // 3. Completing the awaited thing while the thread has a context of its own (a UI thread, some test frameworks).
            Left(HostProbes.Probe("tcs-ConfigureAwait(false)", mode, ambientContextAtDrive: true));
            Left(HostProbes.Probe("fake-delay-ConfigureAwait(false)", mode, ambientContextAtDrive: true));
        }

        static void Left(HostProbes.Observation seen)
        {
            seen.EveryPieceOnThePumpingThread.ShouldBeFalse($"{seen.Body} under {seen.Mode}");
            seen.IdlePumpFinishedTheRun.ShouldBeFalse($"{seen.Body} under {seen.Mode}");
            seen.PumpUntilFinishedTheRun.ShouldBeTrue($"{seen.Body} under {seen.Mode}");
        }
    }

    [Test]
    public void Real_asynchronous_work_comes_back_to_the_pumping_thread_but_only_pump_until_can_wait_for_it()
    {
        foreach (var mode in _onTheHost)
        {
            foreach (var body in new[] { "task-run", "pipe-read", "file-read", "instant-clock-delay" })
            {
                var seen = HostProbes.Probe(body, mode);

                // The await captured the host's context, so the continuation is queued to the host...
                seen.EveryPieceOnThePumpingThread.ShouldBeTrue($"{body} under {mode}");

                // ...at a moment the host cannot foresee. Whether pump-until-idle saw it is a race, so it is not asserted.
                seen.PumpUntilFinishedTheRun.ShouldBeTrue($"{body} under {mode}");
            }
        }
    }

    [Test]
    public void With_bodies_on_the_thread_pool_pump_until_finishes_whatever_the_body_awaits_except_a_fake_clock_it_races()
    {
        foreach (var body in new[] { "tcs", "yield", "save", "task-run", "pipe-read", "stop-cancels-fake-delay-ConfigureAwait(false)" })
        {
            HostProbes.Probe(body, BodyMode.ThreadPool).PumpUntilFinishedTheRun.ShouldBeTrue(body);
        }
    }
}
