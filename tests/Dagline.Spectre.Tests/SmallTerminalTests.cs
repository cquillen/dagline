using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class SmallTerminalTests
{
    [Test]
    [Arguments(59, 24)]
    [Arguments(80, 14)]
    public async Task Below_60_columns_or_15_rows_the_frame_is_the_header_alone(int width, int height)
    {
        var work = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("work", work.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), width, height);
        await work.Started;
        var frame = display.NextFrame();
        var screen = display.Screen;
        work.Release();
        await display.Run;

        frame.Count.ShouldBe(3);
        frame[0].ShouldStartWith("Dagline");
        frame[1].ShouldBe(new string('━', width));
        frame[2].ShouldStartWith("● 1 ");
        screen.OnAlternateScreen.ShouldBeTrue();
    }

    [Test]
    public async Task At_60_by_15_the_panes_are_drawn()
    {
        var work = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("work", work.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 15);
        await work.Started;
        var frame = display.NextFrame();
        work.Release();
        await display.Run;

        frame.ShouldBe(
        [
            "Dagline  no journal" + new string(' ', 27) + "0%  elapsed 0s",
            new string('━', 60),
            "● 1" + new string(' ', 49) + "cap 1/16",
            "── Groups " + new string('─', 50),
            "● work 0/1",
            "── Working (1) " + new string('─', 45),
            "● work" + new string(' ', 52) + "0s",
        ]);
    }
}
