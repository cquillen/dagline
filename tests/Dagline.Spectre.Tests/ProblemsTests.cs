using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class ProblemsTests
{
    [Test]
    public async Task Failures_are_listed_newest_first_with_what_each_blocks_when_it_failed_and_its_message()
    {
        var late = new PausedBody();
        var slow = new PausedBody();
        var builder = new GraphBuilder();
        var early = builder.Task("early", context => throw context.Fail("fail one"));
        builder.Task("x", _ => Task.CompletedTask).Requires(early);
        var lateTask = builder.Task("late", late.Body);
        builder.Task("y", _ => Task.CompletedTask).Requires(lateTask);
        builder.Task("z", _ => Task.CompletedTask).Requires(lateTask);
        builder.Task("slow", slow.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await late.Started;
        await slow.Started;
        await display.Handled("x: pending -> blocked");
        display.Clock.Advance(TimeSpan.FromSeconds(30));
        late.Throw(new InvalidOperationException("Sequence contains no elements"));
        await display.Handled("z: pending -> blocked");
        var frame = display.NextFrame();
        slow.Release();
        await display.Run;

        frame.ShouldBe(
        [
            Pinned("Dagline  no journal", "83%  elapsed 30s", 80),
            new string('━', 80),
            Pinned("● 1  ✗ 2  ⊘ 3", "cap 1/16", 80),
            Rule("Groups", 80),
            "✗ early 0/1 ✗1",
            "⊘ x     0/1",
            "✗ late  0/1 ✗1",
            "⊘ y     0/1",
            "⊘ z     0/1",
            "● slow  0/1",
            Rule("Problems (2 failed) 3 tasks blocked behind 2 failures", 80),
            Pinned("✗ late", "⊘ blocks 2  04:12:30", 80),
            "    Sequence contains no elements",
            Pinned("✗ early", "⊘ blocks 1  04:12:00", 80),
            "    fail one",
        ]);
    }

    [Test]
    public async Task A_failure_that_blocks_nothing_shows_only_when_it_failed_and_the_title_says_nothing_is_blocked()
    {
        var slow = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("broken", context => throw context.Fail("no input"));
        builder.Task("slow", slow.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 20);
        await display.Handled("broken: running -> failed");
        var frame = display.NextFrame();
        slow.Release();
        await display.Run;

        Problems(frame).ShouldBe(
        [
            Rule("Problems (1 failed)", 60),
            Pinned("✗ broken", "04:12:00", 60),
            "    no input",
        ]);
    }

    [Test]
    public async Task Cancelled_tasks_follow_the_failures_newest_first()
    {
        var first = new PausedBody();
        var second = new PausedBody();
        var keep = new PausedBody();
        var stop = new StopSignal();
        var builder = new GraphBuilder();
        var broken = builder.Task("broken", context => throw context.Fail("no input"));
        builder.Task("after", _ => Task.CompletedTask).Requires(broken);
        builder.Task("first", first.Body);
        builder.Task("second", second.Body);
        builder.Task("keep", keep.Body);
        var options = new RunOptions { Journal = Journal.None, GracePeriod = Timeout.InfiniteTimeSpan };

        await using var display = await DisplayRun.StartAsync(builder.Build(), 100, 24, options, stop);
        await first.Started;
        await second.Started;
        await keep.Started;
        await display.Handled("after: pending -> blocked");
        stop.Request();
        await display.Handled("stop requested");
        display.Clock.Advance(TimeSpan.FromSeconds(1));
        first.Throw(new OperationCanceledException());
        await display.Handled("first: running -> cancelled");
        display.Clock.Advance(TimeSpan.FromSeconds(1));
        second.Throw(new OperationCanceledException());
        await display.Handled("second: running -> cancelled");
        var frame = display.NextFrame();
        keep.Release();
        await display.Run;

        Problems(frame).ShouldBe(
        [
            Rule("Problems (1 failed, 2 cancelled) 1 task blocked behind 1 failure", 100),
            Pinned("✗ broken", "⊘ blocks 1  04:12:00", 100),
            "    no input",
            Pinned("■ second", "04:12:02", 100),
            Pinned("■ first", "04:12:01", 100),
        ]);
    }

    [Test]
    public async Task The_latest_three_warnings_are_chosen_by_the_order_their_events_came()
    {
        var a = new PausedBody();
        var b = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);
        builder.Task("b", b.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        var contextA = await a.Started;
        var contextB = await b.Started;
        contextA.Warn("one");
        await display.Handled("warning a: one");
        contextB.Warn("two");
        await display.Handled("warning b: two");
        contextA.Warn("three");
        await display.Handled("warning a: three");
        contextB.Warn("four");
        await display.Handled("warning b: four");
        var frame = display.NextFrame();
        a.Release();
        b.Release();
        await display.Run;

        frame.ShouldBe(
        [
            Pinned("Dagline  no journal", "0%  elapsed 0s", 80),
            new string('━', 80),
            Pinned("● 2  ▲ 4", "cap 2/16", 80),
            Rule("Groups", 80),
            "● a 0/1 ▲2",
            "● b 0/1 ▲2",
            Rule("Problems (4 warnings)", 80),
            "▲ b  four",
            "▲ a  three",
            "▲ b  two",
        ]);
    }

    [Test]
    public async Task A_warning_past_the_number_a_task_keeps_is_counted_but_never_among_the_latest()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", async context =>
        {
            for (var warning = 1; warning <= 101; warning++)
            {
                context.Warn($"w{warning}");
            }

            await a.Body(context);
        });

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await a.Started;
        await display.Handled("warning a: w100");
        var frame = display.NextFrame();
        a.Release();
        await display.Run;

        Problems(frame).ShouldBe(
        [
            Rule("Problems (101 warnings)", 80),
            "▲ a  w100",
            "▲ a  w99",
            "▲ a  w98",
        ]);
    }

    [Test]
    public async Task The_pane_takes_no_lines_when_there_is_nothing_to_show()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);
        builder.Task("b", _ => Task.CompletedTask);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await display.Handled("b: running -> succeeded");
        var frame = display.NextFrame();
        a.Release();
        await display.Run;

        frame.ShouldNotContain(line => line.StartsWith("── Problems", StringComparison.Ordinal));
    }

    /// <summary>The problems pane: the lines of the frame from its title on.</summary>
    private static IEnumerable<string> Problems(IReadOnlyList<string> frame)
        => frame.SkipWhile(line => !line.StartsWith("── Problems", StringComparison.Ordinal));

    /// <summary><paramref name="left"/> at the start of a line <paramref name="width"/> cells wide and <paramref name="right"/> at its end.</summary>
    private static string Pinned(string left, string right, int width) => left + new string(' ', width - left.Length - right.Length) + right;

    /// <summary>A pane's title line: the title in a rule <paramref name="width"/> cells wide.</summary>
    private static string Rule(string title, int width) => "── " + title + " " + new string('─', width - title.Length - 4);
}
