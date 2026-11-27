namespace Dagline;

/// <summary>How a run ended.</summary>
public enum RunOutcome
{
    /// <summary>Every task succeeded. A graph with no tasks is complete.</summary>
    Complete,

    /// <summary>
    /// Nobody asked the run to drain or to stop, it went as far as it could, and at least one task failed or was blocked.
    /// </summary>
    Incomplete,

    /// <summary>The run was drained or stopped and at least one task had not succeeded.</summary>
    Stopped,
}
