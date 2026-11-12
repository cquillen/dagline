using Dagline.TestSupport;

namespace Dagline.Tests;

public class GraphDiffersFromJournalTests
{
    [Test]
    public async Task A_journal_path_not_in_the_graph_is_not_part_of_the_run_and_its_success_applies_again_when_it_returns()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var full = Full();
        var first = await Runner.RunAsync(full.Graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = first.Snapshot.Journal!.Id;

        var smaller = new GraphBuilder();
        smaller.Task("load", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            smaller.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, id), Reporters = [reporter] });
        fail = false;
        var returned = Full();
        var third = await Runner.RunAsync(returned.Graph, new RunOptions { Journal = Journal.Continue(temporary.Path, id) });

        first.Outcome.ShouldBe(RunOutcome.Incomplete);
        reporter.Lines.ShouldBe(["run started", "run ended: complete"]);
        second.Outcome.ShouldBe(RunOutcome.Complete);
        second.Snapshot.Nodes.Count.ShouldBe(1);
        second.Snapshot.TryGetNode(returned.Gone, out _).ShouldBeFalse();
        second.Snapshot.JournalPathsNotInGraph.ShouldBe(2);
        second.Snapshot.JournalPathsNotInGraphUnsucceeded.ShouldBe(1);
        third.Snapshot.JournalPathsNotInGraph.ShouldBe(0);
        third.Snapshot[returned.Gone].IsEarlierSuccess.ShouldBeTrue();
        third.Snapshot[returned.Broken].IsEarlierSuccess.ShouldBeFalse();
        third.Outcome.ShouldBe(RunOutcome.Complete);

        (Graph Graph, NodePath Gone, NodePath Broken) Full()
        {
            var builder = new GraphBuilder();
            builder.Task("load", _ => Task.CompletedTask);
            var gone = builder.Task("gone", _ => Task.CompletedTask);
            var broken = builder.Task("broken", context => fail ? throw context.Fail("bad data") : Task.CompletedTask);
            return (builder.Build(), gone.Path, broken.Path);
        }
    }

    [Test]
    public async Task A_path_that_changed_between_task_and_group_counts_as_not_in_the_graph()
    {
        using var temporary = new TemporaryDirectory();
        var before = new GraphBuilder();
        before.Task("a", _ => Task.CompletedTask);
        before.Group("b").Task("x", _ => Task.CompletedTask);
        var first = await Runner.RunAsync(before.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });

        var after = new GraphBuilder();
        var ax = after.Group("a").Task("x", _ => Task.CompletedTask);
        var b = after.Task("b", _ => Task.CompletedTask);
        var second = await Runner.RunAsync(
            after.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        second.Snapshot.JournalPathsNotInGraph.ShouldBe(2);
        second.Snapshot.JournalPathsNotInGraphUnsucceeded.ShouldBe(0);
        second.Snapshot.EarlierSuccesses.ShouldBe(0);
        second.Snapshot[ax.Path].Started.ShouldNotBeNull();
        second.Snapshot[b.Path].Started.ShouldNotBeNull();
    }

    [Test]
    public async Task The_counts_of_journal_paths_not_in_the_graph_include_a_set_aside_path_that_left_the_graph()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var stop = new StopSignal();
        var succeedsOnceStopped = new StoppedBody(onStop: _ => { });
        var builder = new GraphBuilder();
        var check = builder.Task("check", context =>
        {
            if (fail)
            {
                throw context.Fail("bad data");
            }

            stop.Request();
            return succeedsOnceStopped.Body(context);
        });
        builder.Task("summary", _ => Task.CompletedTask).After(check);
        builder.Task("gone", _ => Task.CompletedTask);
        builder.Task("broken", context => throw context.Fail("bad data"));
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = first.Snapshot.Journal!.Id;
        fail = false;
        var second = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, id) }, stop);

        var smaller = new GraphBuilder();
        smaller.Task("check", _ => Task.CompletedTask);
        var third = await Runner.RunAsync(smaller.Build(), new RunOptions { Journal = Journal.Continue(temporary.Path, id) });

        first.Snapshot.JournalPathsNotInGraph.ShouldBe(0);
        second.Snapshot.JournalPathsNotInGraph.ShouldBe(0);
        third.Snapshot.JournalPathsNotInGraph.ShouldBe(3);
        third.Snapshot.JournalPathsNotInGraphUnsucceeded.ShouldBe(2);
    }

    [Test]
    public async Task Continuing_with_a_smaller_graph_that_completes_gives_a_complete_run_and_counts_the_work_left_behind()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Group("shard-1").Task("load", _ => Task.CompletedTask);
        builder.Group("shard-2").Task("load", context => throw context.Fail("bad data"));
        var first = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });

        var shardOne = new GraphBuilder();
        shardOne.Group("shard-1").Task("load", _ => Task.CompletedTask);
        var second = await Runner.RunAsync(
            shardOne.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        first.Outcome.ShouldBe(RunOutcome.Incomplete);
        second.Outcome.ShouldBe(RunOutcome.Complete);
        second.Snapshot.JournalPathsNotInGraph.ShouldBe(1);
        second.Snapshot.JournalPathsNotInGraphUnsucceeded.ShouldBe(1);
    }

    [Test]
    public async Task A_task_after_a_group_keeps_its_success_with_the_same_graph_and_loses_it_when_the_group_gained_a_task()
    {
        using var temporary = new TemporaryDirectory();
        var summaries = 0;
        var first = await Runner.RunAsync(Shards("shard-3"), new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = first.Snapshot.Journal!.Id;
        var same = await Runner.RunAsync(Shards("shard-3"), new RunOptions { Journal = Journal.Continue(temporary.Path, id) });
        var grown = await Runner.RunAsync(
            Shards("shard-3", "shard-4"),
            new RunOptions { Journal = Journal.Continue(temporary.Path, id) });

        var summary = NodePath.Parse("summary");
        same.Snapshot[summary].IsEarlierSuccess.ShouldBeTrue();
        same.Snapshot.SetAside.ShouldBeEmpty();
        grown.Snapshot.SetAside.ShouldBe([summary]);
        grown.Snapshot[NodePath.Parse("shards/shard-4/load")].Started.ShouldNotBeNull();
        grown.Snapshot.JournalPathsNotInGraph.ShouldBe(0);
        summaries.ShouldBe(2);
        grown.Outcome.ShouldBe(RunOutcome.Complete);

        Graph Shards(params string[] shards)
        {
            var builder = new GraphBuilder();
            var group = builder.Group("shards");
            foreach (var shard in shards)
            {
                group.Group(shard).Task("load", _ => Task.CompletedTask);
            }

            builder.Task("summary", _ =>
            {
                summaries++;
                return Task.CompletedTask;
            }).After(group);
            return builder.Build();
        }
    }
}
