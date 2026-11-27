namespace Dagline.Tests;

/// <summary>
/// The work of starting tasks, counted as slot tries, grows with the number of tasks and not with its square. The step core is
/// driven directly, settling each started task in the order it started, so the count is the same on every run of the test.
/// </summary>
public class SlotTryTests
{
    [Test]
    public void Tasks_behind_one_small_limit_cost_at_most_two_slot_tries_each()
    {
        const int taskCount = 2_000;
        var builder = new GraphBuilder();
        for (var index = 1; index <= taskCount; index++)
        {
            builder.Task($"load-{index}", _ => Task.CompletedTask).Limit("database");
        }

        var options = new RunOptions { Journal = Journal.None, Limits = new Dictionary<string, int> { ["database"] = 4 } };

        var (outcome, tries) = StepThrough(builder.Build(), options);

        var bound = 2 * taskCount + options.MaxRunningTasks;
        outcome.ShouldBe(RunOutcome.Complete);
        tries.ShouldBeLessThanOrEqualTo(bound, $"The run made {tries} slot tries, over the bound of {bound}.");
    }

    [Test]
    public void Tasks_behind_two_small_limits_some_holding_both_cost_at_most_three_slot_tries_each()
    {
        const int taskCount = 2_000;
        var builder = new GraphBuilder();
        for (var index = 1; index <= taskCount; index++)
        {
            var task = builder.Task($"task-{index}", _ => Task.CompletedTask);
            if (index % 5 == 0)
            {
                task.Limit("database").Limit("files");
            }
            else
            {
                task.Limit(index % 2 == 0 ? "database" : "files");
            }
        }

        var options = new RunOptions { Journal = Journal.None, Limits = new Dictionary<string, int> { ["database"] = 4, ["files"] = 4 } };

        var (outcome, tries) = StepThrough(builder.Build(), options);

        var bound = 3 * taskCount;
        outcome.ShouldBe(RunOutcome.Complete);
        tries.ShouldBeLessThanOrEqualTo(bound, $"The run made {tries} slot tries, over the bound of {bound}.");
    }

    /// <summary>
    /// Gives the step core the inputs a run would, settling each started task as a success in the order the steps started them,
    /// and takes the snapshot of every event in order, as the run does.
    /// </summary>
    private static (RunOutcome? Outcome, int SlotTries) StepThrough(Graph graph, RunOptions options)
    {
        var steps = new RunSteps(graph, RunSettings.From(graph, options), earlier: null, new Continuation(graph, earlier: null));
        var now = DateTimeOffset.UnixEpoch;
        var running = new Queue<BuiltTask>();
        RunOutcome? outcome = null;

        CarryOut(steps.Start(now, drained: false, stopped: false, new UnreadSource()));
        CarryOut(steps.EndIfDone(now));
        while (running.TryDequeue(out var task))
        {
            CarryOut(steps.BodyEnded(task, TaskOutcome.Succeeded, now));
            CarryOut(steps.EndIfDone(now));
        }

        return (outcome, steps.SlotTries);

        void CarryOut(IReadOnlyList<Step> decided)
        {
            foreach (var step in decided)
            {
                foreach (var stepEvent in step.Events)
                {
                    steps.SnapshotAfter(stepEvent);
                }

                foreach (var started in step.Starts)
                {
                    running.Enqueue(started);
                }

                if (step.Ends is { } end)
                {
                    outcome = end.Outcome;
                }
            }
        }
    }

    /// <summary>The run as reporters would read it, which no reporter does here: there is none.</summary>
    private sealed class UnreadSource : ISnapshotSource
    {
        public Snapshot Current => throw new InvalidOperationException("No reporter reads the run in these tests.");
    }
}
