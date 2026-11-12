using Dagline.TestSupport;

namespace Dagline.Tests;

public class CheckpointTests
{
    private const int _repeats = 50;

    [Test]
    public async Task A_checkpoint_saved_by_a_task_that_then_fails_is_handed_back_in_the_next_run()
    {
        using var temporary = new TemporaryDirectory();
        var firstRun = true;
        var handedBack = new List<string>();
        var builder = new GraphBuilder();
        builder.Task("export", async context =>
        {
            handedBack.Add(context.Checkpoint);
            if (firstRun)
            {
                await context.SaveCheckpointAsync("job-17");
                throw context.Fail("job 17 failed");
            }
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        firstRun = false;
        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        handedBack.ShouldBe(["", "job-17"]);
    }

    [Test]
    public async Task A_checkpoint_saved_by_a_task_that_is_then_cancelled_is_handed_back_in_the_next_run()
    {
        using var temporary = new TemporaryDirectory();
        var firstRun = true;
        var stop = new StopSignal();
        var handedBack = new List<string>();
        var builder = new GraphBuilder();
        var export = builder.Task("export", async context =>
        {
            handedBack.Add(context.Checkpoint);
            if (firstRun)
            {
                await context.SaveCheckpointAsync("job-17");
                stop.Request();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) }, stop);
        firstRun = false;
        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        first.Snapshot[export.Path].Status.ShouldBe(NodeStatus.Cancelled);
        handedBack.ShouldBe(["", "job-17"]);
    }

    [Test]
    public async Task A_checkpoint_saved_by_a_process_that_is_then_killed_is_handed_back_because_the_save_returns_once_it_is_on_disk()
    {
        using var temporary = new TemporaryDirectory();
        using var copies = new TemporaryDirectory();
        var firstRun = true;
        var handedBack = new List<string>();
        var builder = new GraphBuilder();
        builder.Task("export", async context =>
        {
            handedBack.Add(context.Checkpoint);
            if (firstRun)
            {
                await context.SaveCheckpointAsync("job-17");
                copies.CopyIn(Directory.GetFiles(temporary.Path, "*.journal").Single());
            }
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        firstRun = false;
        var fromTheCopy = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(copies.Path, first.Snapshot.Journal!.Id) });

        fromTheCopy.Outcome.ShouldBe(RunOutcome.Complete);
        handedBack.ShouldBe(["", "job-17"]);
    }

    [Test]
    public async Task A_checkpoint_saved_by_a_task_that_succeeds_is_discarded_and_the_success_is_kept_when_the_path_leaves_the_graph_and_returns()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        var export = builder.Task("export", async context => await context.SaveCheckpointAsync("job-17"));
        var graph = builder.Build();
        var without = new GraphBuilder();
        without.Task("other", _ => Task.CompletedTask);

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = first.Snapshot.Journal!.Id;
        await Runner.RunAsync(without.Build(), new RunOptions { Journal = Journal.Continue(temporary.Path, id) });
        var third = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, id) });

        third.Snapshot[export.Path].IsEarlierSuccess.ShouldBeTrue();
    }

    [Test]
    public async Task Checkpoint_is_empty_when_there_is_none_and_reads_back_the_latest_save_or_clearing_within_a_run()
    {
        var seen = new List<string>();
        var builder = new GraphBuilder();
        builder.Task("export", async context =>
        {
            seen.Add(context.Checkpoint);
            await context.SaveCheckpointAsync("job-17");
            seen.Add(context.Checkpoint);
            await context.SaveCheckpointAsync("job-18");
            seen.Add(context.Checkpoint);
            await context.ClearCheckpointAsync();
            seen.Add(context.Checkpoint);
            await context.SaveCheckpointAsync("job-19");
            seen.Add(context.Checkpoint);
            await context.SaveCheckpointAsync("");
            seen.Add(context.Checkpoint);
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        seen.ShouldBe(["", "job-17", "job-18", "", "job-19", ""]);
    }

    [Test]
    public async Task Saving_again_replaces_the_checkpoint_handed_back()
    {
        using var temporary = new TemporaryDirectory();
        var firstRun = true;
        var handedBack = new List<string>();
        var builder = new GraphBuilder();
        builder.Task("export", async context =>
        {
            handedBack.Add(context.Checkpoint);
            if (firstRun)
            {
                await context.SaveCheckpointAsync("job-17");
                await context.SaveCheckpointAsync("job-18");
                throw context.Fail("job 18 failed");
            }
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        firstRun = false;
        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        handedBack.ShouldBe(["", "job-18"]);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_checkpoint_cleared_before_a_reported_failure_is_not_handed_back(bool byAnEmptySave)
    {
        using var temporary = new TemporaryDirectory();
        var firstRun = true;
        var handedBack = new List<string>();
        var builder = new GraphBuilder();
        builder.Task("export", async context =>
        {
            handedBack.Add(context.Checkpoint);
            if (firstRun)
            {
                await context.SaveCheckpointAsync("job-17");
                await (byAnEmptySave ? context.SaveCheckpointAsync("") : context.ClearCheckpointAsync());
                throw context.Fail("job 17 failed: start fresh work next time");
            }
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        firstRun = false;
        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        handedBack.ShouldBe(["", ""]);
    }

    [Test]
    public async Task Clearing_when_there_is_no_checkpoint_writes_nothing()
    {
        using var temporary = new TemporaryDirectory();
        var lengths = new List<long>();
        var builder = new GraphBuilder();
        builder.Task("export", async context =>
        {
            var journal = new FileInfo(Directory.GetFiles(temporary.Path, "*.journal").Single());
            lengths.Add(journal.Length);
            await context.ClearCheckpointAsync();
            await context.SaveCheckpointAsync("");
            journal.Refresh();
            lengths.Add(journal.Length);
            await context.SaveCheckpointAsync("job-17");
            journal.Refresh();
            lengths.Add(journal.Length);
        });

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });

        lengths[1].ShouldBe(lengths[0]);
        lengths[2].ShouldBeGreaterThan(lengths[1]);
    }

    [Test]
    public async Task A_checkpoint_of_exactly_4096_bytes_as_UTF8_is_kept_and_one_byte_more_is_refused_at_the_call()
    {
        using var temporary = new TemporaryDirectory();
        var firstRun = true;
        var fits = new string('x', 4096);
        var fitsInWideCharacters = new string('é', 2048);
        var handedBack = new List<string>();
        var refused = new List<ArgumentException>();
        var builder = new GraphBuilder();
        builder.Task("ascii", async context =>
        {
            handedBack.Add(context.Checkpoint);
            if (firstRun)
            {
                refused.Add(Should.Throw<ArgumentException>(() => context.SaveCheckpointAsync(fits + "x")));
                await context.SaveCheckpointAsync(fits);
                throw context.Fail("job failed");
            }
        });
        builder.Task("wide", async context =>
        {
            handedBack.Add(context.Checkpoint);
            if (firstRun)
            {
                refused.Add(Should.Throw<ArgumentException>(() => context.SaveCheckpointAsync(fitsInWideCharacters + "é")));
                await context.SaveCheckpointAsync(fitsInWideCharacters);
                throw context.Fail("job failed");
            }
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path), Cap = 1 });
        firstRun = false;
        await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), Cap = 1 });

        refused.Select(exception => exception.ParamName).ShouldBe(["text", "text"]);
        handedBack.ShouldBe(["", "", fits, fitsInWideCharacters]);
    }

    [Test]
    public async Task A_null_checkpoint_is_refused_at_the_call()
    {
        var builder = new GraphBuilder();
        builder.Task("export", context =>
        {
            Should.Throw<ArgumentNullException>(() => context.SaveCheckpointAsync(null!)).ParamName.ShouldBe("text");
            return Task.CompletedTask;
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task A_save_or_clearing_whose_token_is_already_signalled_is_cancelled_and_changes_nothing()
    {
        using var temporary = new TemporaryDirectory();
        var firstRun = true;
        var handedBack = new List<string>();
        var builder = new GraphBuilder();
        builder.Task("export", async context =>
        {
            handedBack.Add(context.Checkpoint);
            if (firstRun)
            {
                await context.SaveCheckpointAsync("job-17");
                await Should.ThrowAsync<OperationCanceledException>(context.SaveCheckpointAsync("job-18", new CancellationToken(canceled: true)));
                await Should.ThrowAsync<OperationCanceledException>(context.ClearCheckpointAsync(new CancellationToken(canceled: true)));
                context.Checkpoint.ShouldBe("job-17");
                throw context.Fail("job 17 failed");
            }
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        firstRun = false;
        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        first.Snapshot.Failed.Single().Message.ShouldBe("job 17 failed");
        handedBack.ShouldBe(["", "job-17"]);
    }

    [Test]
    public async Task A_save_made_during_the_grace_period_succeeds_and_is_handed_back_in_the_next_run()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            using var temporary = new TemporaryDirectory();
            var firstRun = true;
            var stop = new StopSignal();
            var handedBack = new List<string>();
            var builder = new GraphBuilder();
            var export = builder.Task("export", async context =>
            {
                handedBack.Add(context.Checkpoint);
                if (firstRun)
                {
                    stop.Request();
                    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (context.CancellationToken.Register(() => stopped.TrySetResult()))
                    {
                        await stopped.Task;
                    }

                    await context.SaveCheckpointAsync("job-17", CancellationToken.None);
                    context.CancellationToken.ThrowIfCancellationRequested();
                }
            });
            var graph = builder.Build();

            var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) }, stop);
            firstRun = false;
            await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

            first.Snapshot[export.Path].Status.ShouldBe(NodeStatus.Cancelled);
            handedBack.ShouldBe(["", "job-17"]);
        }
    }

    [Test]
    public async Task A_save_or_clearing_after_the_run_has_let_go_of_the_task_throws_and_is_not_handed_back()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            using var temporary = new TemporaryDirectory();
            var export = new PausedBody();
            var builder = new GraphBuilder();
            var exportTask = builder.Task("export", export.Body);
            var graph = builder.Build();
            var stop = new StopSignal();

            var run = Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) }, stop);
            var context = await export.Started;
            await context.SaveCheckpointAsync("job-17");
            stop.Request();
            stop.Request();
            var first = await run;
            await Should.ThrowAsync<InvalidOperationException>(context.SaveCheckpointAsync("job-18"));
            await Should.ThrowAsync<InvalidOperationException>(context.ClearCheckpointAsync());
            export.Release();
            var handedBack = "";
            var again = new GraphBuilder();
            again.Task("export", nextContext =>
            {
                handedBack = nextContext.Checkpoint;
                return Task.CompletedTask;
            });
            await Runner.RunAsync(again.Build(), new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

            first.Snapshot[exportTask.Path].Status.ShouldBe(NodeStatus.Cancelled);
            context.Checkpoint.ShouldBe("job-17");
            handedBack.ShouldBe("job-17");
        }
    }

    [Test]
    public async Task A_save_or_clearing_after_the_task_has_settled_throws()
    {
        TaskContext? kept = null;
        var builder = new GraphBuilder();
        builder.Task("export", async context =>
        {
            kept = context;
            await context.SaveCheckpointAsync("job-17");
        });

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        await Should.ThrowAsync<InvalidOperationException>(kept!.SaveCheckpointAsync("job-18"));
        await Should.ThrowAsync<InvalidOperationException>(kept.ClearCheckpointAsync());
        kept.Checkpoint.ShouldBe("job-17");
    }

    [Test]
    public async Task A_checkpoint_is_in_no_event_and_no_snapshot()
    {
        using var temporary = new TemporaryDirectory();
        const string checkpoint = "job-4f9c1e";
        var reporter = new RecordingReporter();
        var builder = new GraphBuilder();
        var export = builder.Task("export", async context =>
        {
            await context.SaveCheckpointAsync(checkpoint);
            throw context.Fail("job failed");
        });

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.New(temporary.Path), Reporters = [reporter] });

        reporter.Lines.ShouldBe(
        [
            "run started",
            "export: pending -> ready",
            "export: ready -> running",
            "export: running -> failed",
            "run ended: incomplete",
        ]);
        reporter.Events.ShouldAllBe(runEvent => !runEvent.ToString().Contains(checkpoint, StringComparison.Ordinal));
        var node = result.Snapshot[export.Path];
        node.ToString().ShouldNotContain(checkpoint);
        new[] { node.Message, node.StatusText, node.WaitingText }.Concat(node.Detail).Concat(node.Warnings)
            .ShouldAllBe(text => text == null || !text.Contains(checkpoint, StringComparison.Ordinal));
    }
}
