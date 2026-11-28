namespace Dagline;

/// <summary>
/// What runs things for a run's loop: the seam between the loop and the threads (ADR 0015). It is public for
/// <c>Dagline.Testing</c>, whose scripted run supplies a host that a test pumps on its own thread. An app never implements it:
/// <see cref="Runner.Start"/> and <see cref="Runner.RunAsync"/> run every run on a host of Dagline's own that does each thing on
/// the thread pool.
/// </summary>
/// <remarks>
/// <para>
/// The loop asks its host for five things: to run its next turn, to start a task body, to hand events to a reporter, to signal a
/// token, and to say how a task the loop completes resumes whoever awaits it. The host decides where each happens. The loop
/// itself only ever runs inside <see cref="RunLoop"/>, one turn at a time, and a turn applies every input waiting in the run's
/// inbox before it returns.
/// </para>
/// <para>
/// A host must never run any of these inline inside the call that asks for it: the call may come from the loop itself, from a
/// task's thread or from a reporter's, and none of them may run another's code. Each member may be called from any thread.
/// </para>
/// </remarks>
public interface IRunHost
{
    /// <summary>
    /// Runs the loop's next turn. The run calls it once when it starts, and the run's inbox calls it each time an input is posted
    /// while the loop waits, from whichever thread posts. The host runs <paramref name="turn"/> later, never inline on that thread,
    /// and never two turns at once.
    /// </summary>
    /// <param name="turn">The loop's turn. It returns once the inbox is empty or the loop has gone.</param>
    void RunLoop(Action turn);

    /// <summary>
    /// Starts a task body. When the host starts the body under a synchronization context of its own, every await in the body that
    /// resumes on its context, and the end of the body, which posts to the run, come back to the host as well.
    /// </summary>
    /// <param name="body">Calls the task's body and, once it has ended, tells the run how. It never throws.</param>
    void StartBody(Func<Task> body);

    /// <summary>Runs one reporter's delivery, which hands the reporter the events queued for it, one call at a time, until none is left.</summary>
    /// <param name="delivery">The delivery. It never throws: a reporter that throws is dropped inside it.</param>
    void DeliverToReporter(Func<Task> delivery);

    /// <summary>
    /// Signals a token, for a stop of the tasks: the token reads as cancelled when this returns. The callbacks registered on it are
    /// consumer code: in an app they must not run on the loop, and an exception one of them throws is the consumer's affair, which
    /// must not come out of this call.
    /// </summary>
    /// <param name="source">The source of the token to signal.</param>
    void Signal(CancellationTokenSource source);

    /// <summary>
    /// The options the loop creates the tasks it completes with, such as the run's result and the answer to a checkpoint save:
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> in an app, so that no awaiter runs on the loop, or
    /// <see cref="TaskCreationOptions.None"/> under a host a test pumps, so that an awaiter resumes inline, on the pumping thread.
    /// </summary>
    TaskCreationOptions CompletionOptions { get; }
}
