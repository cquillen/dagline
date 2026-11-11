namespace Dagline;

/// <summary>
/// The work of a task: an asynchronous method over the task's context.
/// </summary>
/// <param name="context">
/// What the run gives this task: its path, the run's clock, and the means to report progress, warnings and an outcome.
/// </param>
/// <returns>A <see cref="Task"/> that completes when the work has ended. The task succeeds when it completes without an exception.</returns>
/// <remarks>
/// <para>
/// A run starts every task body on the thread pool, so work done before the first <see langword="await"/> does not hold up other tasks.
/// A body captures the services it needs: Dagline has no dependency injection.
/// </para>
/// <para>
/// An exception that escapes the body fails that task and nothing else: the run carries on, and the exception is never thrown
/// from <see cref="Runner.RunAsync"/>. The task ends <see cref="NodeStatus.Failed"/> as an unexpected failure
/// (<see cref="FailureKind.Unexpected"/>), with the exception's message, on one line, as its <see cref="NodeSnapshot.Message"/> and the exception's
/// type and stack trace added to its <see cref="NodeSnapshot.Detail"/>. It makes no difference whether the body throws before its first
/// <see langword="await"/>, throws after it, or returns a task that is faulted or cancelled. A cancellation exception is treated
/// like any other until the run is stopped. After a stop it makes the task <see cref="NodeStatus.Cancelled"/>:
/// <see cref="TaskContext.CancellationToken"/> says how a task ends once a stop has been requested.
/// </para>
/// <para>
/// A body that has called <see cref="TaskContext.Fail"/> ends as a reported failure (<see cref="FailureKind.Reported"/>) instead,
/// however it ends: by throwing the exception that call returned, by returning, or by letting another exception escape.
/// </para>
/// </remarks>
public delegate Task TaskBody(TaskContext context);
