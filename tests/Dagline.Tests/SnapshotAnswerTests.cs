using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class SnapshotAnswerTests
{
    private const int _repeats = 50;
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task A_run_mid_flight_answers_warnings_time_ran_waiting_working_and_what_a_failure_blocked()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new FakeTimeProvider(_runStart);
        var earlierGraph = new GraphBuilder();
        earlierGraph.Task("earlier", _ => Task.CompletedTask);
        var first = await Runner.RunAsync(earlierGraph.Build(), new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = clock });

        var checking = new PausedBody();
        var noisyWork = new PausedBody();
        var quietWork = new PausedBody();
        var firstPoll = new PausedBody();
        var secondPoll = new PausedBody();
        var builder = new GraphBuilder();
        var earlier = builder.Task("earlier", _ => Task.CompletedTask);
        var check = builder.Task("check", async context =>
        {
            await checking.Body(context);
            throw context.Fail("bad data");
        });
        var load = builder.Task("load", _ => Task.CompletedTask).Requires(check);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var pollA = builder.Task("poll-a", async context =>
        {
            using (context.BeginWaiting("job 17 to finish"))
            {
                await firstPoll.Body(context);
            }
        }).After(check);
        var noisy = builder.Task("noisy", async context =>
        {
            for (var entity = 1; entity <= 102; entity++)
            {
                context.AddWarning($"entity {entity} has no region");
            }

            await noisyWork.Body(context);
        }).After(check);
        var quiet = builder.Task("quiet", quietWork.Body).After(check);
        var pollB = builder.Task("poll-b", async context =>
        {
            using (context.BeginWaiting())
            {
                await secondPoll.Body(context);
            }
        }).After(check);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id),
                TimeProvider = clock,
                Reporters = [reporter],
            });
        var source = await reporter.Source;
        await checking.Started;
        clock.Advance(TimeSpan.FromSeconds(5));
        checking.Release();
        await Task.WhenAll(noisyWork.Started, quietWork.Started, firstPoll.Started, secondPoll.Started);
        var midway = source.Current;
        clock.Advance(TimeSpan.FromSeconds(7));
        noisyWork.Release();
        quietWork.Release();
        firstPoll.Release();
        secondPoll.Release();
        var result = await run;

        midway.Waiting.Select(task => task.Path).ShouldBe([pollA.Path, pollB.Path]);
        midway.Working.Select(task => task.Path).ShouldBe([noisy.Path, quiet.Path]);
        midway[pollA.Path].IsWaiting.ShouldBeTrue();
        midway[pollA.Path].IsWorking.ShouldBeFalse();
        midway[noisy.Path].IsWaiting.ShouldBeFalse();
        midway[noisy.Path].IsWorking.ShouldBeTrue();
        midway[check.Path].IsWorking.ShouldBeFalse();
        midway[earlier.Path].IsWorking.ShouldBeFalse();
        midway[noisy.Path].WarningCount.ShouldBe(102);
        midway[noisy.Path].Warnings.Count.ShouldBe(100);
        midway[quiet.Path].WarningCount.ShouldBe(0);
        midway[check.Path].Ran.ShouldBe(TimeSpan.FromSeconds(5));
        midway[noisy.Path].Ran.ShouldBeNull();
        midway[load.Path].Ran.ShouldBeNull();
        midway[earlier.Path].IsEarlierSuccess.ShouldBeTrue();
        midway[earlier.Path].Ran.ShouldBeNull();
        midway.TryGetBlocked(check.Path, out var blocked).ShouldBeTrue();
        blocked!.Tasks.Select(task => task.Path).ShouldBe([load.Path, publish.Path]);
        midway.TryGetBlocked(noisy.Path, out var none).ShouldBeFalse();
        none.ShouldBeNull();

        var final = result.Snapshot;
        final.Waiting.ShouldBeEmpty();
        final.Working.ShouldBeEmpty();
        final[pollA.Path].IsWaiting.ShouldBeFalse();
        final[noisy.Path].IsWorking.ShouldBeFalse();
        final[noisy.Path].WarningCount.ShouldBe(102);
        final[noisy.Path].Ran.ShouldBe(TimeSpan.FromSeconds(7));
        final[check.Path].Ran.ShouldBe(TimeSpan.FromSeconds(5));
        final[earlier.Path].Ran.ShouldBeNull();
        final.TryGetBlocked(check.Path, out var stillBlocked).ShouldBeTrue();
        stillBlocked.ShouldBe(final.Blocked.Single());
    }

    [Test]
    public async Task The_facts_of_a_stop_the_app_asked_for_say_the_app_asked()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var stop = new StopSignal();
            var builder = new GraphBuilder();
            builder.Task("load", async context =>
            {
                stop.Request();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            });
            var reporter = new RecordingReporter();

            var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, stop);

            var requested = reporter.Events.ToList().FindIndex(runEvent => runEvent is StopRequested);
            reporter.Snapshots[requested].Stop!.By.ShouldBe(StopRequester.App);
            result.Snapshot.Stop!.By.ShouldBe(StopRequester.App);
        }
    }

    [Test]
    public async Task Empty_text_a_task_reports_is_none_and_waiting_text_is_one_line()
    {
        var brokenWait = new PausedBody();
        var blankWait = new PausedBody();
        var builder = new GraphBuilder();
        var quiet = builder.Task("quiet", context =>
        {
            context.SetMessage("");
            return Task.CompletedTask;
        });
        var retracted = builder.Task("retracted", context =>
        {
            context.SetMessage("loaded 40 entities");
            context.SetMessage("");
            return Task.CompletedTask;
        });
        var rejected = builder.Task("rejected", context => throw context.Fail(""));
        var broken = builder.Task("broken", async context =>
        {
            using (context.BeginWaiting("job 2\r\nqueued\nbehind job 1"))
            {
                await brokenWait.Body(context);
            }
        });
        var blank = builder.Task("blank", async context =>
        {
            using (context.BeginWaiting(""))
            {
                await blankWait.Body(context);
            }
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await Task.WhenAll(brokenWait.Started, blankWait.Started);
        var waiting = source.Current;
        brokenWait.Release();
        blankWait.Release();
        var result = await run;

        result.Snapshot[quiet.Path].Message.ShouldBeNull();
        result.Snapshot[retracted.Path].Message.ShouldBeNull();
        result.Snapshot[rejected.Path].Failure.ShouldBe(FailureKind.Reported);
        result.Snapshot[rejected.Path].Message.ShouldBeNull();
        waiting[broken.Path].WaitingText.ShouldBe("job 2 queued behind job 1");
        waiting[blank.Path].WaitingSince.ShouldNotBeNull();
        waiting[blank.Path].WaitingText.ShouldBeNull();
        var waits = reporter.Events.OfType<WaitingChanged>().ToList();
        waits.Where(wait => wait.Path == broken.Path).Select(wait => wait.Text).ShouldBe(["job 2 queued behind job 1", "job 2 queued behind job 1"]);
        waits.Where(wait => wait.Path == blank.Path).Select(wait => wait.Text).ShouldBe([null, null]);
    }
}
