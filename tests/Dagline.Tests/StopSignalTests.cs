namespace Dagline.Tests;

public class StopSignalTests
{
    [Test]
    public void A_new_signal_has_not_been_requested()
    {
        var stop = new StopSignal();

        stop.IsRequested.ShouldBeFalse();
        stop.Requests.ShouldBe(0);
    }

    [Test]
    public void Each_request_is_counted()
    {
        var stop = new StopSignal();

        stop.Request();
        stop.IsRequested.ShouldBeTrue();
        stop.Requests.ShouldBe(1);

        stop.Request();
        stop.Request();
        stop.Requests.ShouldBe(3);
    }

    [Test]
    public async Task Requests_made_at_once_from_many_threads_are_each_counted()
    {
        var stop = new StopSignal();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var requesters = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await go.Task;
            stop.Request();
        })).ToList();
        go.SetResult();
        await Task.WhenAll(requesters);

        stop.Requests.ShouldBe(64);
    }
}
