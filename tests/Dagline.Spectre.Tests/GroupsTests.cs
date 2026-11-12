using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class GroupsTests
{
    [Test]
    public async Task There_is_a_row_per_group_two_levels_down_and_per_top_level_node_with_no_group_beneath_it()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var validate = builder.Group("validate");
        var first = validate.Group("shard-1");
        first.Task("a", a.Body);
        first.Task("b", _ => Task.CompletedTask);
        var second = validate.Group("shard-2");
        second.Task("c", context =>
        {
            context.Warn("odd");
            return Task.CompletedTask;
        });
        second.Task("d", context => throw context.Fail("broken"));
        builder.Group("finish").Title("Wrap up").Task("report", _ => Task.CompletedTask).After(validate);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 20);
        await a.Started;
        await display.Handled("load: running -> succeeded");
        await display.Handled("validate/shard-1/b: running -> succeeded");
        await display.Handled("validate/shard-2/c: running -> succeeded");
        await display.Handled("validate/shard-2/d: running -> failed");
        var frame = display.NextFrame();
        a.Release();
        await display.Run;

        frame.ShouldBe(
        [
            "Dagline  no journal                          66%  elapsed 0s",
            new string('━', 50) + new string('─', 10),
            "✓ 3  ● 1  · 1  ✗ 1  ▲ 1                             cap 1/16",
            "── Groups " + new string('─', 50),
            "✓ load    1/1",
            "● shard-1 ●✓ 1/2",
            "✗ shard-2 ✓✗ 1/2 ✗1 ▲1",
            "· Wrap up · 0/1",
            "── Working (1) " + new string('─', 45),
            "● validate/shard-1/a" + new string(' ', 38) + "0s",
        ]);
    }

    [Test]
    public async Task When_the_two_level_rows_do_not_fit_there_is_a_row_per_top_level_node()
    {
        var x = new PausedBody();
        var e = new PausedBody();
        var f = new PausedBody();
        var builder = new GraphBuilder();
        var prepare = builder.Group("prepare");
        prepare.Task("p1", _ => Task.CompletedTask);
        prepare.Task("p2", _ => Task.CompletedTask);
        var validate = builder.Group("validate");
        validate.Group("shard-1").Task("x", x.Body);
        validate.Group("shard-2").Task("y", context =>
        {
            context.Warn("odd");
            throw context.Fail("broken");
        });
        validate.Group("shard-3").Task("z", _ => Task.CompletedTask);
        var process = builder.Group("process");
        process.Group("shard-1").Task("e", Waiting(e));
        process.Group("shard-2").Task("f", Waiting(f));
        var finish = builder.Group("finish");
        finish.Task("t1", _ => Task.CompletedTask).After(process);
        finish.Task("t2", _ => Task.CompletedTask).After(process);
        finish.Task("t3", _ => Task.CompletedTask).After(process);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 10);
        await x.Started;
        await display.Handled("waiting process/shard-1/e: job");
        await display.Handled("waiting process/shard-2/f: job");
        await display.Handled("prepare/p1: running -> succeeded");
        await display.Handled("prepare/p2: running -> succeeded");
        await display.Handled("validate/shard-2/y: running -> failed");
        await display.Handled("validate/shard-3/z: running -> succeeded");
        var frame = display.NextFrame();
        x.Release();
        e.Release();
        f.Release();
        await display.Run;

        frame.Skip(3).Take(5).ShouldBe(
        [
            "── Groups " + new string('─', 50),
            "✓ prepare  ✓✓ 2/2",
            "● validate ●✗✓ 1/3 ✗1 ▲1",
            "◌ process  ◌◌ 0/2",
            "· finish   ··· 0/3",
        ]);
    }

    [Test]
    public async Task A_row_with_more_children_than_cells_shows_a_bar_split_by_status()
    {
        var gate = new PausedBody();
        var builder = new GraphBuilder();
        var first = builder.Task("gate", gate.Body);
        var big = builder.Group("big");
        for (var i = 1; i <= 20; i++)
        {
            big.Task($"done-{i}", _ => Task.CompletedTask);
            big.Task($"later-{i}", _ => Task.CompletedTask).After(first);
        }

        await using var display = await DisplayRun.StartAsync(builder.Build(), 40, 12);
        await gate.Started;
        for (var i = 1; i <= 20; i++)
        {
            await display.Handled($"big/done-{i}: running -> succeeded");
        }

        var frame = display.NextFrame();
        var overflowed = display.Screen.Overflowed;
        gate.Release();
        await display.Run;

        overflowed.ShouldBeFalse();

        frame.Skip(3).Take(3).ShouldBe(
        [
            "── Groups " + new string('─', 30),
            "● gate 0/1",
            "· big  " + new string('━', 13) + new string('─', 14) + " 20/40",
        ]);
    }

    [Test]
    public async Task An_earlier_success_is_a_dimmer_tick_than_a_success_of_this_run()
    {
        using var temporary = new TemporaryDirectory();
        var first = new GraphBuilder();
        var earlierGroup = first.Group("g");
        earlierGroup.Task("a", _ => Task.CompletedTask);
        earlierGroup.Task("b", _ => throw new InvalidOperationException("not yet"));
        var earlier = await Runner.RunAsync(first.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        var b = new PausedBody();
        var second = new GraphBuilder();
        var group = second.Group("g");
        group.Task("a", _ => Task.CompletedTask);
        group.Task("b", b.Body);
        group.Task("c", _ => Task.CompletedTask);
        var options = new RunOptions { Journal = Journal.Continue(temporary.Path, earlier.Snapshot.Journal!.Id) };

        await using var display = await DisplayRun.StartAsync(second.Build(), 60, 20, options);
        await b.Started;
        await display.Handled("g/c: running -> succeeded");
        var frame = display.NextFrame();
        var screen = display.Screen;
        b.Release();
        await display.Run;

        frame[4].ShouldBe("● g ✓●✓ 2/3");
        screen.StyleAt(4, 4).ShouldBe("38;5;22");
        screen.StyleAt(4, 6).ShouldBe("38;5;2");
    }

    [Test]
    public async Task A_graph_with_no_groups_has_a_row_per_task()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);
        builder.Task("b", _ => Task.CompletedTask).Title("Second");

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 20);
        await a.Started;
        await display.Handled("b: running -> succeeded");
        var frame = display.NextFrame();
        a.Release();
        await display.Run;

        frame.Skip(3).Take(3).ShouldBe(
        [
            "── Groups " + new string('─', 50),
            "● a      0/1",
            "✓ Second 1/1",
        ]);
    }

    [Test]
    public async Task A_graph_with_no_nodes_draws_the_header_alone()
    {
        await using var display = await DisplayRun.StartAsync(new GraphBuilder().Build(), 60, 20);
        var result = await display.Run;

        result.ReporterErrors.ShouldBeEmpty();
        display.Screen.Frames.ShouldBeGreaterThanOrEqualTo(1);
        display.Summary.ShouldContain(line => line.StartsWith("Run complete", StringComparison.Ordinal));
    }

    private static TaskBody Waiting(PausedBody body) => async context =>
    {
        using var waiting = context.Waiting("job");
        await body.Body(context);
    };
}
