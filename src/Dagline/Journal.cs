namespace Dagline;

/// <summary>
/// The app's choice of journal for a run. The choice is required, so that running with no journal is visible in the code.
/// </summary>
/// <remarks>The set of choices is closed: a type outside Dagline cannot derive from this record.</remarks>
public abstract record Journal
{
    private protected Journal()
    {
    }

    /// <summary>No journal: the run records nothing, and a later run cannot continue it.</summary>
    public static Journal None { get; } = new NoJournal();

    private sealed record NoJournal : Journal;
}
