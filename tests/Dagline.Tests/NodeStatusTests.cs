namespace Dagline.Tests;

public class NodeStatusTests
{
    [Test]
    [Arguments(NodeStatus.Pending, false)]
    [Arguments(NodeStatus.Ready, false)]
    [Arguments(NodeStatus.Running, false)]
    [Arguments(NodeStatus.Succeeded, true)]
    [Arguments(NodeStatus.Failed, true)]
    [Arguments(NodeStatus.Blocked, true)]
    [Arguments(NodeStatus.Cancelled, true)]
    public void IsSettled_is_true_for_succeeded_failed_blocked_and_cancelled_only(NodeStatus status, bool settled)
    {
        status.IsSettled().ShouldBe(settled);
    }
}
