namespace Dagline;

/// <summary>
/// What <see cref="RunSteps"/> needs of the journal a run is on: what the journal held when it was opened, and the facts of this run
/// that its run-started entry records. A run with no journal has none of these.
/// </summary>
/// <param name="Earlier">The journal as it was folded when it was opened.</param>
/// <param name="User">The operating system user the run's process runs as.</param>
/// <param name="Machine">The name of the machine the run's process runs on.</param>
internal sealed record RunJournal(JournalFold Earlier, string User, string Machine)
{
    /// <summary>The journal as this run's snapshots show it.</summary>
    public JournalInfo Info { get; } = Earlier.NextRun;

    /// <summary>What this run needs of the journal <paramref name="file"/>, with the user and machine of this process.</summary>
    public static RunJournal Of(JournalFile file) => new(file.Fold, Environment.UserName, Environment.MachineName);
}
