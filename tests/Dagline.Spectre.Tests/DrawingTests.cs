using System.Diagnostics;
using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class DrawingTests
{
    [Test]
    public async Task The_display_takes_the_alternate_screen_for_the_run_and_leaves_the_summary_on_the_ordinary_screen()
    {
        var work = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("work", work.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        var during = display.Screen;
        work.Release();
        await display.Run;
        var after = display.Screen;

        during.OnAlternateScreen.ShouldBeTrue();
        during.CursorVisible.ShouldBeFalse();
        during.Frames.ShouldBe(1);
        during.Frame[0].ShouldStartWith("Dagline");
        during.Ordinary.ShouldBeEmpty();
        after.OnAlternateScreen.ShouldBeFalse();
        after.CursorVisible.ShouldBeTrue();
        after.Modes.ShouldBe(["?1049h", "?25l", "?1049l", "?25h"]);
        after.Ordinary.ShouldContain(line => line.StartsWith("Run complete", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_frame_is_drawn_for_each_redraw_interval_and_none_after_the_run_ends()
    {
        var work = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("work", work.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await work.Started;
        display.Clock.Advance(DisplayRun.RedrawInterval);
        var afterOne = display.Screen.Frames;
        display.Clock.Advance(DisplayRun.RedrawInterval);
        display.Clock.Advance(DisplayRun.RedrawInterval);
        var afterThree = display.Screen.Frames;
        work.Release();
        await display.Run;
        var written = display.Console.Output.Length;
        display.Clock.Advance(DisplayRun.RedrawInterval * 8);

        afterOne.ShouldBe(2);
        afterThree.ShouldBe(4);
        display.Console.Output.Length.ShouldBe(written);
    }

    [Test]
    public async Task A_frame_shows_a_change_that_the_display_has_not_yet_been_handed_as_an_event()
    {
        var builder = new GraphBuilder();
        builder.Task("work", _ => Task.CompletedTask);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24, holdBefore: "work: pending -> ready");
        var first = display.LastFrame;
        IReadOnlyList<string> frame;
        var waited = Stopwatch.StartNew();
        do
        {
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "no frame showed the task succeeded");
            frame = display.NextFrame();
            await Task.Yield();
        }
        while (!frame[0].Contains("100%", StringComparison.Ordinal));

        display.ReleaseHeld();
        await display.Run;

        first[0].ShouldContain(" 0%");
        frame[2].ShouldStartWith("✓ 1 ");
    }

    [Test]
    public async Task A_console_resized_between_frames_gets_a_frame_at_the_new_size()
    {
        var work = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("work", work.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await work.Started;
        var wide = display.NextFrame();
        display.Resize(40, 2);
        var narrow = display.NextFrame();
        var screen = display.Screen;
        work.Release();
        await display.Run;

        wide[1].Length.ShouldBe(80);
        narrow.Count.ShouldBe(2);
        narrow[1].Length.ShouldBe(40);
        screen.Overflowed.ShouldBeFalse();
    }

    [Test]
    [Arguments(100, 32)]
    [Arguments(60, 15)]
    [Arguments(20, 3)]
    [Arguments(12, 2)]
    [Arguments(1, 1)]
    public async Task No_line_of_a_frame_is_wider_than_the_console_and_no_frame_is_taller(int width, int height)
    {
        var working = new PausedBody();
        var builder = new GraphBuilder();
        var group = builder.Group("validate").Title("a long description of the group");
        group.Task("first", working.Body);
        group.Task("second", _ => Task.CompletedTask).Requires(group.Task("third", _ => throw new InvalidOperationException("no")));
        builder.Task("later", _ => Task.CompletedTask).After(group);
        var options = new RunOptions { Journal = Journal.None, Cap = 96 };

        await using var display = await DisplayRun.StartAsync(builder.Build(), width, height, options);
        await display.Handled("validate/second: pending -> blocked");
        var frame = display.NextFrame();
        var screen = display.Screen;
        working.Release();
        await display.Run;

        frame.Count.ShouldBeLessThanOrEqualTo(height);
        frame.ShouldAllBe(line => line.Length <= width);
        screen.Overflowed.ShouldBeFalse();
    }
}
