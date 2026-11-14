using Microsoft.Extensions.Time.Testing;

namespace Dagline.Testing.Tests;

public class TaskHarnessTests
{
    [Test]
    public async Task Wrapping_a_body_does_not_run_it()
    {
        var ran = false;

        await using var harness = TaskHarness.For(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        ran.ShouldBeFalse();
    }

    [Test]
    public async Task A_task_that_succeeds_comes_back_with_its_message_and_warnings()
    {
        await using var harness = TaskHarness.For(context =>
        {
            context.AddWarning("the export was slow");
            context.SetMessage("17 rows exported");
            return Task.CompletedTask;
        });

        var outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(NodeStatus.Succeeded);
        outcome.Failure.ShouldBeNull();
        outcome.Message.ShouldBe("17 rows exported");
        outcome.Detail.ShouldBeEmpty();
        outcome.Warnings.ShouldBe(["the export was slow"]);
        outcome.Waited.ShouldBeFalse();
    }

    [Test]
    public async Task A_reported_failure_comes_back_with_its_kind_message_and_detail_lines()
    {
        await using var harness = TaskHarness.For(context => throw context.Fail("job 17 failed", "step 3 timed out", "retry later"));

        var outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(NodeStatus.Failed);
        outcome.Failure.ShouldBe(FailureKind.Reported);
        outcome.Message.ShouldBe("job 17 failed");
        outcome.Detail.ShouldBe(["step 3 timed out", "retry later"]);
    }

    [Test]
    public async Task An_exception_from_the_body_comes_back_as_an_unexpected_failure()
    {
        await using var harness = TaskHarness.For(_ => throw new InvalidOperationException("the file was locked"));

        var outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(NodeStatus.Failed);
        outcome.Failure.ShouldBe(FailureKind.Unexpected);
        outcome.Message.ShouldBe("the file was locked");
        outcome.Detail.ShouldContain("System.InvalidOperationException");
    }

    [Test]
    public async Task The_warnings_of_a_task_that_then_fails_are_in_its_outcome()
    {
        await using var harness = TaskHarness.For(context =>
        {
            context.AddWarning("the first source was empty");
            context.AddWarning("the second source was empty");
            throw context.Fail("nothing to export");
        });

        var outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(NodeStatus.Failed);
        outcome.Warnings.ShouldBe(["the first source was empty", "the second source was empty"]);
    }

    [Test]
    public async Task A_task_that_waits_through_the_helper_completes_without_real_waiting_and_is_seen_to_have_waited()
    {
        var checks = 0;
        DateTimeOffset? started = null;
        DateTimeOffset? finished = null;
        await using var harness = TaskHarness.For(async context =>
        {
            started = context.TimeProvider.GetUtcNow();
            await context.WaitUntilAsync(TimeSpan.FromHours(1), _ => Task.FromResult(++checks == 3), "the export to finish");
            finished = context.TimeProvider.GetUtcNow();
        });

        var outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(NodeStatus.Succeeded);
        outcome.Waited.ShouldBeTrue();
        checks.ShouldBe(3);
        (finished - started).ShouldBe(TimeSpan.FromHours(2));
    }

    [Test]
    public async Task The_outcome_holds_the_status_text_the_task_last_set()
    {
        await using var harness = TaskHarness.For(context =>
        {
            context.SetStatusText("reading rows");
            context.SetStatusText("writing 17 rows");
            return Task.CompletedTask;
        });

        var outcome = await harness.RunAsync();

        outcome.StatusText.ShouldBe("writing 17 rows");
    }

    [Test]
    public async Task A_task_that_never_sets_status_text_or_waits_has_neither_in_its_outcome()
    {
        await using var harness = TaskHarness.For(_ => Task.CompletedTask);

        var outcome = await harness.RunAsync();

        outcome.StatusText.ShouldBeNull();
        outcome.WaitingText.ShouldBeNull();
    }

    [Test]
    public async Task The_outcome_holds_the_text_of_the_last_wait_the_task_began_after_the_wait_has_ended()
    {
        await using var harness = TaskHarness.For(async context =>
        {
            await context.WaitUntilAsync(TimeSpan.FromMinutes(1), _ => Task.FromResult(true), "the export to start");
            await context.WaitUntilAsync(TimeSpan.FromMinutes(1), _ => Task.FromResult(true), "the export to finish");
        });

        var outcome = await harness.RunAsync();

        outcome.WaitingText.ShouldBe("the export to finish");
    }

    [Test]
    public async Task A_clock_passed_in_is_the_one_the_body_sees()
    {
        var clock = new FakeTimeProvider();
        TimeProvider? seen = null;
        await using var harness = TaskHarness.For(
            context =>
            {
                seen = context.TimeProvider;
                return Task.CompletedTask;
            },
            clock);

        await harness.RunAsync();

        seen.ShouldBeSameAs(clock);
    }

    [Test]
    public async Task A_check_that_never_returns_true_ends_with_the_guard_exception_on_the_default_clock()
    {
        await using var harness = TaskHarness.For(context => context.WaitUntilAsync(TimeSpan.FromHours(1), _ => Task.FromResult(false)));

        await Should.ThrowAsync<ClockGuardException>(harness.RunAsync());
    }

    [Test]
    public async Task A_check_that_never_returns_true_ends_with_the_guard_exception_on_an_instant_clock_passed_in()
    {
        var checks = 0;
        await using var harness = TaskHarness.For(
            context => context.WaitUntilAsync(
                TimeSpan.FromHours(1),
                _ =>
                {
                    checks++;
                    return Task.FromResult(false);
                }),
            new InstantClock(TimeSpan.FromHours(3)));

        await Should.ThrowAsync<ClockGuardException>(harness.RunAsync());
        checks.ShouldBe(5);
    }

    [Test]
    public async Task The_run_is_on_a_new_journal_in_a_temporary_directory_that_disposal_deletes()
    {
        var clock = new FakeTimeProvider(UniqueStart());
        var harness = TaskHarness.For(_ => Task.CompletedTask, timeProvider: clock);

        await harness.RunAsync();
        var journal = FindJournal(clock.GetUtcNow());
        journal.ShouldNotBeNull();
        var directory = Path.GetDirectoryName(journal)!;
        Path.GetDirectoryName(directory).ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
        Path.GetFileName(directory).ShouldStartWith("dagline-harness-");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(directory).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await harness.DisposeAsync();

        Directory.Exists(directory).ShouldBeFalse();
    }

    [Test]
    public async Task Disposing_twice_is_harmless_and_a_run_after_disposal_is_refused()
    {
        var harness = TaskHarness.For(_ => Task.CompletedTask);
        await harness.RunAsync();

        await harness.DisposeAsync();
        await harness.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(harness.RunAsync());
    }

    [Test]
    public async Task Disposing_a_harness_that_never_ran_is_harmless()
    {
        var harness = TaskHarness.For(_ => Task.CompletedTask);

        await harness.DisposeAsync();
    }

    [Test]
    public async Task An_exception_from_the_run_itself_comes_out_unchanged()
    {
        var broken = new InvalidOperationException("the clock is broken");
        await using var harness = TaskHarness.For(_ => Task.CompletedTask, new BrokenClock(broken));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(harness.RunAsync());

        thrown.ShouldBeSameAs(broken);
    }

    private static DateTimeOffset UniqueStart() =>
        new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(Random.Shared.NextInt64(1_000_000_000));

    private static string? FindJournal(DateTimeOffset createdAt)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(Path.GetTempPath(), $"{createdAt:yyyyMMdd'T'HHmmss'Z'}-*.journal", options).SingleOrDefault();
    }

    private sealed class BrokenClock(Exception broken) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw broken;
    }
}
