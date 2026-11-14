namespace Dagline;

/// <summary>Why the grace ended: why a run stopped waiting for its running tasks after a stop, as <see cref="GraceEnded"/> gives it.</summary>
public enum GraceEndReason
{
    /// <summary>The <see cref="StopSignal"/> was requested a second time.</summary>
    SecondStop,

    /// <summary>The grace period, <see cref="RunOptions.GracePeriod"/>, ran out.</summary>
    GracePeriodOver,
}
