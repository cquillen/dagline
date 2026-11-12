using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class SpaceTests
{
    // After the header (3 lines) and the groups (4 lines), a run with 12 working tasks, 8 waiting and 6 failures leaves
    // height - 7 lines. Problems take up to a third of them, waiting up to five tasks, working the rest, and lines no pane
    // can use stay blank. Every pane gets its title and one more line before any pane grows.
    [Test]
    [Arguments(15, 2, 4, 2)]
    [Arguments(20, 2, 7, 4)]
    [Arguments(30, 9, 7, 6)]
    [Arguments(40, 13, 7, 10)]
    public async Task The_panes_share_the_lines_left_by_one_rule_and_each_says_how_many_it_left_out(
        int height,
        int workingLines,
        int waitingLines,
        int problemsLines)
    {
        await using var run = await CrowdedRun.StartAsync(height, waiting: 8);
        var frame = run.Display.NextFrame();
        var screen = run.Display.Screen;
        await run.EndAsync();

        frame.Count.ShouldBeLessThanOrEqualTo(height);
        screen.Overflowed.ShouldBeFalse();
        var working = Pane(frame, "Working");
        var waiting = Pane(frame, "Waiting");
        var problems = Pane(frame, "Problems");
        working.Count.ShouldBe(workingLines);
        waiting.Count.ShouldBe(waitingLines);
        problems.Count.ShouldBe(problemsLines);

        var workingShown = working.Count(line => line.StartsWith("● ", StringComparison.Ordinal));
        if (workingShown < 12)
        {
            working[^1].ShouldBe($"  and {12 - workingShown} more working");
        }

        var waitingShown = waiting.Count(line => line.StartsWith("◌ ", StringComparison.Ordinal));
        waitingShown.ShouldBeLessThanOrEqualTo(5);
        waiting[^1].ShouldBe($"  and {8 - waitingShown} more, the newest waiting 0s");

        var failuresShown = problems.Count(line => line.StartsWith("✗ ", StringComparison.Ordinal));
        if (failuresShown < 6)
        {
            problems[^1].ShouldBe($"  and {6 - failuresShown} more {(6 - failuresShown == 1 ? "failure" : "failures")}");
        }
    }

    [Test]
    public async Task At_30_rows_the_frame_has_every_pane_cut_to_its_share()
    {
        await using var run = await CrowdedRun.StartAsync(30, waiting: 8);
        var frame = run.Display.NextFrame();
        await run.EndAsync();

        frame.Skip(7).ShouldBe(
        [
            Rule("Working (12)"),
            .. Enumerable.Range(1, 7).Select(i => Pinned($"● work/w{i}", "0s")),
            "  and 5 more working",
            Rule("Waiting on external systems (8) longest 0s"),
            .. Enumerable.Range(1, 5).Select(i => Pinned($"◌ wait/a{i}   job", "0s")),
            "  and 3 more, the newest waiting 0s",
            Rule("Problems (6 failed)"),
            Pinned("✗ bad/f1", "04:12:00"),
            "    broken",
            Pinned("✗ bad/f2", "04:12:00"),
            "    broken",
            "  and 4 more failures",
        ]);
    }

    [Test]
    public async Task A_pane_with_nothing_to_show_takes_no_lines_and_problems_take_no_more_than_a_third()
    {
        // Two groups leave 20 - 6 = 14 lines: working needs 2 and problems get a third, 4, so the other 8 stay blank.
        await using var run = await CrowdedRun.StartAsync(20, waiting: 0, working: 1);
        var frame = run.Display.NextFrame();
        await run.EndAsync();

        frame.ShouldNotContain(line => line.StartsWith("── Waiting", StringComparison.Ordinal));
        frame.Count.ShouldBe(12);
        Pane(frame, "Working").Count.ShouldBe(2);
        Pane(frame, "Problems").ShouldBe(
        [
            Rule("Problems (6 failed)"),
            Pinned("✗ bad/f1", "04:12:00"),
            "    broken",
            "  and 5 more failures",
        ]);
    }

    [Test]
    public async Task A_line_too_few_for_a_pane_goes_to_another_pane_and_is_not_left_blank()
    {
        // The header (3) and the groups (9) leave 3 lines: working takes 2 first, which leaves 1, too few for waiting's 2.
        List<PausedBody> bodies = [];
        var builder = new GraphBuilder();
        var work = builder.Group("work");
        for (var i = 1; i <= 3; i++)
        {
            var body = new PausedBody();
            bodies.Add(body);
            work.Task($"w{i}", body.Body);
        }

        var waited = new PausedBody();
        bodies.Add(waited);
        builder.Group("wait").Task("a1", async context =>
        {
            using var job = context.Waiting("job");
            await waited.Body(context);
        });
        for (var i = 1; i <= 6; i++)
        {
            builder.Task($"p{i}", _ => Task.CompletedTask).After(work);
        }

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 15);
        foreach (var body in bodies)
        {
            await body.Started;
        }

        await display.Handled("waiting wait/a1: job");
        var frame = display.NextFrame();
        foreach (var body in bodies)
        {
            body.Release();
        }

        await display.Run;

        frame.Count.ShouldBe(15);
        frame.Skip(12).ShouldBe(
        [
            Rule("Working (3)"),
            Pinned("● work/w1", "0s"),
            "  and 2 more working",
        ]);
    }

    /// <summary>The lines of the pane whose title starts with <paramref name="title"/>, up to the next pane's title.</summary>
    private static List<string> Pane(IReadOnlyList<string> frame, string title)
    {
        var start = frame.SkipWhile(line => !line.StartsWith("── " + title, StringComparison.Ordinal)).ToList();
        return start.Count == 0 ? [] : [start[0], .. start.Skip(1).TakeWhile(line => !line.StartsWith("── ", StringComparison.Ordinal))];
    }

    private static string Pinned(string left, string right) => left + new string(' ', 80 - left.Length - right.Length) + right;

    private static string Rule(string title) => "── " + title + " " + new string('─', 80 - title.Length - 4);

    /// <summary>
    /// A run at 80 columns with more working tasks, waiting tasks and failures than a frame can show: tasks in a group
    /// <c>work</c> that are working, in <c>wait</c> that are waiting, and six in <c>bad</c> that fail at once.
    /// </summary>
    private sealed class CrowdedRun : IAsyncDisposable
    {
        private readonly List<PausedBody> _bodies;

        private CrowdedRun(DisplayRun display, List<PausedBody> bodies)
        {
            Display = display;
            _bodies = bodies;
        }

        public DisplayRun Display { get; }

        public static async Task<CrowdedRun> StartAsync(int height, int waiting, int working = 12)
        {
            List<PausedBody> bodies = [];
            var builder = new GraphBuilder();
            var work = builder.Group("work");
            for (var i = 1; i <= working; i++)
            {
                var body = new PausedBody();
                bodies.Add(body);
                work.Task($"w{i}", body.Body);
            }

            if (waiting > 0)
            {
                var wait = builder.Group("wait");
                for (var i = 1; i <= waiting; i++)
                {
                    var body = new PausedBody();
                    bodies.Add(body);
                    wait.Task($"a{i}", async context =>
                    {
                        using var job = context.Waiting("job");
                        await body.Body(context);
                    });
                }
            }

            var bad = builder.Group("bad");
            for (var i = 1; i <= 6; i++)
            {
                bad.Task($"f{i}", context => throw context.Fail("broken"));
            }

            var display = await DisplayRun.StartAsync(builder.Build(), 80, height, new RunOptions { Journal = Journal.None, Cap = 96 });
            foreach (var body in bodies)
            {
                await body.Started;
            }

            for (var i = 1; i <= waiting; i++)
            {
                await display.Handled($"waiting wait/a{i}: job");
            }

            for (var i = 1; i <= 6; i++)
            {
                await display.Handled($"bad/f{i}: running -> failed");
            }

            return new CrowdedRun(display, bodies);
        }

        public async Task EndAsync()
        {
            foreach (var body in _bodies)
            {
                body.Release();
            }

            await Display.Run;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var body in _bodies)
            {
                body.Release();
            }

            await Display.DisposeAsync();
        }
    }
}
