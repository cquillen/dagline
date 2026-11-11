namespace Dagline;

/// <summary>
/// The work of a task: an asynchronous method over the task's context.
/// </summary>
/// <param name="context">What the run gives this task: its path and the run's clock.</param>
/// <returns>A <see cref="Task"/> that completes when the work has ended. The task succeeds when it completes without an exception.</returns>
/// <remarks>
/// A run starts every task body on the thread pool, so work done before the first <see langword="await"/> does not hold up other tasks.
/// A body captures the services it needs: Dagline has no dependency injection.
/// </remarks>
public delegate Task TaskBody(TaskContext context);
