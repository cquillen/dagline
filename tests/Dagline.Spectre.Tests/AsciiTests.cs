using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class AsciiTests
{
    [Test]
    public async Task With_Ascii_set_a_whole_frame_and_a_whole_summary_contain_no_character_outside_ASCII()
    {
        using var temporary = new TemporaryDirectory();
        var work = new PausedBody();
        var wait = new PausedBody();
        var cancel = new PausedBody();
        var stop = new StopSignal();
        var builder = new GraphBuilder();
        builder.Task("ok", context =>
        {
            context.Warn("odd");
            return Task.CompletedTask;
        });
        builder.Task("after", _ => Task.CompletedTask).Requires(builder.Task("bad", context => throw context.Fail("no input")));
        builder.Task("work", context =>
        {
            context.Count(1, 4);
            context.Status("a long status text that the ellipsis has to cut off before the time at the end");
            return work.Body(context);
        });
        builder.Task("wait", async context =>
        {
            using var job = context.Waiting("job");
            await wait.Body(context);
        });
        builder.Task("cancel", cancel.Body);
        builder.Task("later", _ => Task.CompletedTask).Requires(builder.Task("held", _ => Task.CompletedTask));
        var options = new RunOptions { Journal = Journal.New(temporary.Path, "all shards"), Cap = 3 };

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 30, options, stop, ascii: true);
        await work.Started;
        await cancel.Started;
        await display.Handled("waiting wait: job");
        await display.Handled("after: pending -> blocked");
        var running = display.NextFrame();
        stop.Request();
        await display.Handled("stop requested");
        cancel.Throw(new OperationCanceledException());
        await display.Handled("cancel: running -> cancelled");
        var stopping = display.NextFrame();
        work.Release();
        wait.Release();
        var summary = await display.EndAsync();

        running[1].ShouldBe(new string('=', 60) + new string('-', 20));
        running[2].ShouldStartWith("+ 1  * 2  ~ 1  o 1  . 1  x 1  / 1  ! 1  ");
        running[2].ShouldEndWith("cap 3/3 | 1 ready, held");
        running.ShouldContain(line => line.StartsWith("-- Working (2) ---", StringComparison.Ordinal));
        running.ShouldContain(line => line.Contains("...", StringComparison.Ordinal));
        stopping.ShouldContain(line => line.StartsWith(" STOPPING  waiting for 2 running tasks to end | ", StringComparison.Ordinal));
        stopping.ShouldContain(line => line.StartsWith("# cancel", StringComparison.Ordinal));
        summary.ShouldContain(line => line.StartsWith("x bad ", StringComparison.Ordinal));
        foreach (var line in running.Concat(stopping).Concat(summary))
        {
            line.ShouldAllBe(character => char.IsAscii(character), line);
        }
    }
}
