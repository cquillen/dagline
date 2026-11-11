namespace Dagline.Tests;

public class RunOutcomeTests
{
    [Test]
    [Arguments(RunOutcome.Complete, 0)]
    [Arguments(RunOutcome.Incomplete, 1)]
    [Arguments(RunOutcome.Stopped, 2)]
    public void ToExitCode_gives_0_for_complete_1_for_incomplete_and_2_for_stopped(RunOutcome outcome, int exitCode)
    {
        outcome.ToExitCode().ShouldBe(exitCode);
    }

    [Test]
    public void ToExitCode_rejects_a_value_that_is_not_a_run_outcome()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ((RunOutcome)3).ToExitCode());
    }
}
