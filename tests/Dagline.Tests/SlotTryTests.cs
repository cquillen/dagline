namespace Dagline.Tests;

/// <summary>
/// The work of starting tasks, counted as slot tries, grows with the number of tasks and not with its square. The new engine's
/// step core is driven directly, settling each started task in the order it started, so the count is the same on every run of
/// the test.
/// </summary>
public class SlotTryTests
{
    [Test]
    public void Tasks_behind_one_small_limit_cost_at_most_two_slot_tries_each_plus_the_max_running_tasks()
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
    /// Gives the new engine's step core the inputs a run would, settling each started task as a success in the order its body was
    /// started, and builds the snapshot of every event in order, as the loop does. With no journal, no input waits for a write.
    /// </summary>
    private static (RunOutcome? Outcome, int SlotTries) StepThrough(Graph graph, RunOptions options)
    {
        var core = new StepCore(graph, RunSettings.From(graph, options), journal: null, new UnreadSource());
        var now = DateTimeOffset.UnixEpoch;
        var running = new Queue<BuiltTask>();
        RunOutcome? outcome = null;

        CarryOut(core.Apply(Input.Start, now));
        while (running.TryDequeue(out var task))
        {
            CarryOut(core.Apply(Input.BodyEnded(task, TaskOutcome.Succeeded), now));
        }

        return (outcome, core.SlotTries);

        void CarryOut(IReadOnlyList<Effect> effects)
        {
            foreach (var effect in effects)
            {
                switch (effect.Kind)
                {
                    case EffectKind.Publish:
                        core.SnapshotAfter(effect.Change);
                        break;
                    case EffectKind.StartBody:
                        running.Enqueue(effect.Task!);
                        break;
                    case EffectKind.EndRun:
                        outcome = effect.Outcome;
                        break;
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
