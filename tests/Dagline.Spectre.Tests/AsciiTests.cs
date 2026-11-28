using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Testing;

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
        using var stop = new CancellationTokenSource();
        var builder = new GraphBuilder();
        builder.Task("ok", context =>
        {
            context.AddWarning("odd café");
            return Task.CompletedTask;
        }).Title("Größe");
        builder.Task("after", _ => Task.CompletedTask).Requires(builder.Task("bad", context => throw context.Fail("no input ✗", "détail 🙂")));
        builder.Task("work", context =>
        {
            context.SetCount(1, 4);
            context.SetStatusText("a long status text… that the ellipsis has to cut off before the time at the end");
            return work.Body(context);
        });
        builder.Task("wait", async context =>
        {
            using var job = context.BeginWaiting("job → 17");
            await wait.Body(context);
        });
        builder.Task("cancel", async context =>
        {
            context.SetMessage("arrêté");
            await cancel.Body(context);
        });
        builder.Task("later", _ => Task.CompletedTask).Requires(builder.Task("held", _ => Task.CompletedTask));
        var options = new RunOptions { Journal = Journal.New(temporary.Path, "all shards ✓"), MaxRunningTasks = 3 };

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 30, options, ascii: true, stop: stop.Token);
        await work.Started;
        await cancel.Started;
        await display.Handled("waiting wait: job → 17");
        await display.Handled("warning ok: odd café");
        await display.Handled("after: pending -> blocked");
        var running = display.NextFrame();
        await stop.CancelAsync();
        await display.Handled("stop requested");
        cancel.Throw(new OperationCanceledException());
        await display.Handled("cancel: running -> cancelled");
        var stopping = display.NextFrame();
        work.Release();
        wait.Release();
        var summary = await display.EndAsync();

        running[0].ShouldStartWith("Dagline  all shards ?  journal ");
        running[1].ShouldBe(new string('=', 60) + new string('-', 20));
        running[2].ShouldStartWith("+ 1  * 2  ~ 1  o 1  . 1  x 1  / 1  ! 1  ");
        running[2].ShouldEndWith("cap 3/3 | 1 ready, held");
        running.ShouldContain(line => line.StartsWith("+ Gr??e ", StringComparison.Ordinal));
        running.ShouldContain(line => line.StartsWith("-- Working (2) ---", StringComparison.Ordinal));
        running.ShouldContain(line => line.StartsWith("* work", StringComparison.Ordinal) && line.Contains("a long status text? that", StringComparison.Ordinal));
        running.ShouldContain(line => line.StartsWith("~ wait", StringComparison.Ordinal) && line.Contains("job ? 17", StringComparison.Ordinal));
        running.ShouldContain("    no input ?");
        running.ShouldContain(line => line.StartsWith("! ok  odd caf?", StringComparison.Ordinal));
        running.ShouldContain(line => line.Contains("...", StringComparison.Ordinal));
        stopping.ShouldContain(line => line.StartsWith(" STOPPING  waiting for 2 running tasks to end | ", StringComparison.Ordinal));
        stopping.ShouldContain(line => line.StartsWith("# cancel  arr?t?", StringComparison.Ordinal));
        summary.ShouldContain(line => line.StartsWith("x bad ", StringComparison.Ordinal));
        summary.ShouldContain("    no input ?");
        summary.ShouldContain("    d?tail ?");
        summary.ShouldContain("    odd caf?");
        summary.ShouldContain("# cancel  arr?t?");
        foreach (var line in running.Concat(stopping).Concat(summary))
        {
            line.ShouldAllBe(character => char.IsAscii(character), line);
        }
    }

    [Test]
    public async Task With_Ascii_set_the_journal_error_an_unrecorded_run_ended_with_is_drawn_in_ASCII()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var journal = JournalWriteFailure.SettleOf("load").WithMessage("disk full — no space").In(Journal.New(temporary.Path));

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 20, new RunOptions { Journal = journal }, ascii: true);
        await display.Run;

        display.Summary[2].ShouldBe("    The journal could no longer be written, so it is missing some of this run: disk full ? no space");
    }
}
