namespace Dagline;

/// <summary>
/// A task's checkpoint save, as it travels through the run's inbox (ADR 0015): the text to save, which is empty for a clearing, and
/// the completion that answers the task. The loop answers it once the core has decided it, after its entry is written when the run
/// is on a journal; the inbox answers it on the posting thread once the loop has gone.
/// </summary>
/// <param name="text">The checkpoint, or empty to clear it.</param>
/// <param name="completionOptions">How the answer resumes whoever awaits it, as the run's host says.</param>
internal sealed class CheckpointSave(string text, TaskCreationOptions completionOptions)
{
    private readonly TaskCompletionSource _answer = new(completionOptions);

    /// <summary>The checkpoint to save, or empty to clear it.</summary>
    public string Text => text;

    /// <summary>Completes once the save is made, or faults with what refused it.</summary>
    public Task Answered => _answer.Task;

    /// <summary>The save is made. Answered already, it changes nothing.</summary>
    public void Made() => _answer.TrySetResult();

    /// <summary>The save is refused with <paramref name="refusal"/>. Answered already, it changes nothing.</summary>
    public void Refuse(Exception refusal) => _answer.TrySetException(refusal);

    /// <summary>Refuses the save of <paramref name="task"/> because the run's loop has gone, so nothing would keep it.</summary>
    public void RefuseAsTheRunHasEnded(BuiltTask task)
        => Refuse(new InvalidOperationException(
            $"The task '{task.Path}' cannot change its checkpoint: the run has ended, so it no longer keeps anything a task does. "
            + "A task changes its checkpoint only while it is running."));
}
