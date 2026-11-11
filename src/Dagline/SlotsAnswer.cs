namespace Dagline;

/// <summary>What came of trying to take the slots a task needs to start, as <see cref="Slots.Take"/> answers it.</summary>
internal enum SlotsAnswer
{
    /// <summary>The task now holds a slot of the cap and of every limit it holds, and may start.</summary>
    Took,

    /// <summary>A limit the task holds is full. The task took nothing, and a task after it may still fit.</summary>
    LimitFull,

    /// <summary>The cap is full. The task took nothing, and no task can start until one settles.</summary>
    CapFull,
}
