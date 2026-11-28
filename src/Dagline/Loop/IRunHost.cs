namespace Dagline;

/// <summary>
/// What runs things for a run's loop (ADR 0015): the seam between the loop and the threads. The loop asks its host for five
/// things, and the host decides where each happens: on the thread pool in an app, or on a test's own thread when the test pumps.
/// </summary>
/// <remarks>
/// Its members name only types that can be made public, so that <c>Dagline.Testing</c> can supply a host of its own once the seam
/// is public, with no opening into the core's internals.
/// </remarks>
internal interface IRunHost
{
    /// <summary>
    /// Runs the loop's next turn. The run calls it once to begin the loop, and the inbox calls it when an input is posted while the
    /// loop waits, from whichever thread posts: the host must never run the turn inline on that thread.
    /// </summary>
    void RunLoop(Action turn);

    /// <summary>
    /// Starts a task body. Whatever the body's awaits resume on that the host chose, such as a synchronization context it installs
    /// for the body, the end of the body comes back through it as well.
    /// </summary>
    void StartBody(Func<Task> body);

    /// <summary>Runs one reporter's delivery, which hands it the events queued for it until none is left.</summary>
    void DeliverToReporter(Func<Task> delivery);

    /// <summary>
    /// Signals a token: it reads as cancelled when this returns. The callbacks registered on it are consumer code, which must not
    /// run on the loop in an app.
    /// </summary>
    void Signal(CancellationTokenSource source);

    /// <summary>
    /// How a task that the loop completes, such as the run's result, resumes whoever awaits it: on the thread pool in an app, so that
    /// no awaiter runs on the loop, or inline, on the pumping thread, under a host a test pumps.
    /// </summary>
    TaskCreationOptions CompletionOptions { get; }
}
