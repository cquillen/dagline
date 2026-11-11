namespace Dagline;

/// <summary>Why a run stopped waiting for its running tasks after a stop, as <see cref="StoppedWaiting"/> gives it.</summary>
public enum StoppedWaitingReason
{
    /// <summary>The <see cref="StopSignal"/> was requested a second time.</summary>
    SecondStop,

    /// <summary>The grace period, <see cref="RunOptions.GracePeriod"/>, ran out.</summary>
    GracePeriodOver,
}
