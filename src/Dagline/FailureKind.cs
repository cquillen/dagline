namespace Dagline;

/// <summary>Why a task failed: whether the task declared the failure itself or its code broke.</summary>
public enum FailureKind
{
    /// <summary>A reported failure: the task declared it, such as bad data it found or external work that ended in error.</summary>
    Reported,

    /// <summary>An unexpected failure: the task did not declare it. An exception escaped the task body.</summary>
    Unexpected,
}
