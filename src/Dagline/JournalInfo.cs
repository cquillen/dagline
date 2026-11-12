namespace Dagline;

/// <summary>
/// The journal a run is on, as <see cref="Snapshot.Journal"/> shows it: enough to print which journal it is and to continue it later.
/// </summary>
/// <param name="Id">
/// The journal's id, which <see cref="Journal.Continue"/> takes: the UTC creation time as <c>yyyyMMddTHHmmssZ</c>, a hyphen and
/// six lowercase hex digits, as in <c>20261111T041200Z-a3f9c2</c>. A new journal's id is known only from here.
/// </param>
/// <param name="Description">The description the journal was created with, or <see langword="null"/> when it has none.</param>
/// <param name="Run">Which run of the journal this is, counting from 1: a new journal's run is 1, and each run that continues it adds one.</param>
public sealed record JournalInfo(string Id, string? Description, int Run);
