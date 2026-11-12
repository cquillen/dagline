using System.Text.Json.Serialization;

namespace Dagline;

/// <summary>
/// One line of a journal: one JSON object, whose <c>entry</c> property says which kind it is. Every time is UTC,
/// as <see cref="TimeProvider.GetUtcNow"/> gives it.
/// </summary>
/// <remarks>
/// <para>
/// The kinds are the format of the file, and <see cref="JournalFile"/> is the one type that reads and writes them.
/// The format is readable and is not a stable contract: property names are whole words, and a test never asserts on them.
/// A new kind of entry is a record here with its own <see cref="JsonDerivedTypeAttribute"/>, and a case in <see cref="JournalFold"/>
/// when it changes what a later run reads.
/// </para>
/// <para>
/// Progress, waiting, pending, ready and the status of a group are never recorded.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "entry")]
[JsonDerivedType(typeof(HeaderEntry), "header")]
[JsonDerivedType(typeof(RunStartedEntry), "runStarted")]
[JsonDerivedType(typeof(TaskStartedEntry), "taskStarted")]
[JsonDerivedType(typeof(TaskSettledEntry), "taskSettled")]
[JsonDerivedType(typeof(RunEndedEntry), "runEnded")]
[JsonDerivedType(typeof(SetAsideEntry), "setAside")]
internal abstract record JournalEntry;

/// <summary>The first line of every journal.</summary>
/// <param name="Version">The format version, which <see cref="JournalFile"/> checks before reading anything else.</param>
/// <param name="Id">The journal's id, as <see cref="JournalId"/> makes it.</param>
/// <param name="Created">When the journal was created, on the clock of its first run.</param>
/// <param name="Description">The description given at creation, or <see langword="null"/> for none.</param>
internal sealed record HeaderEntry(int Version, string Id, DateTimeOffset Created, string? Description) : JournalEntry;

/// <summary>A run of the journal started.</summary>
/// <param name="Time">When the run started.</param>
/// <param name="User">The operating system user the run's process ran as.</param>
/// <param name="Machine">The name of the machine the run's process ran on.</param>
/// <param name="Cap">The cap of the run.</param>
/// <param name="Limits">The size of each limit the run's graph names, by name.</param>
/// <param name="PathsNotInGraph">How many paths the journal held that are not a task of the run's graph.</param>
/// <param name="PathsNotInGraphUnsucceeded">How many of those had no success on record, or one that was set aside.</param>
internal sealed record RunStartedEntry(
    DateTimeOffset Time,
    string User,
    string Machine,
    int Cap,
    IReadOnlyDictionary<string, int> Limits,
    int PathsNotInGraph,
    int PathsNotInGraphUnsucceeded) : JournalEntry;

/// <summary>
/// A run set aside the earlier success of a task that is <em>after</em> something which was not an earlier success in that run.
/// From here on the path has no success on record, until a later settle gives it one.
/// </summary>
/// <param name="Path">The path of the task.</param>
/// <param name="Time">When the run that set it aside started.</param>
internal sealed record SetAsideEntry(string Path, DateTimeOffset Time) : JournalEntry;

/// <summary>A task started running.</summary>
/// <param name="Path">The path of the task.</param>
/// <param name="Time">When it started.</param>
internal sealed record TaskStartedEntry(string Path, DateTimeOffset Time) : JournalEntry;

/// <summary>A task settled, with its whole outcome.</summary>
/// <param name="Path">The path of the task.</param>
/// <param name="Time">When it settled.</param>
/// <param name="Status">The status it settled with: succeeded, failed, blocked or cancelled.</param>
/// <param name="Message">The one-line message of its outcome, or <see langword="null"/> for none.</param>
/// <param name="Detail">The detail lines kept.</param>
/// <param name="DetailOmitted">How many detail lines were not kept.</param>
/// <param name="Warnings">The warnings kept.</param>
/// <param name="WarningsOmitted">How many warnings were not kept.</param>
/// <param name="Failure">Why it failed, for a failed task.</param>
/// <param name="BlockedBy">The path of the failed task that blocked it, for a blocked task.</param>
internal sealed record TaskSettledEntry(
    string Path,
    DateTimeOffset Time,
    NodeStatus Status,
    string? Message,
    IReadOnlyList<string> Detail,
    int DetailOmitted,
    IReadOnlyList<string> Warnings,
    int WarningsOmitted,
    FailureKind? Failure,
    string? BlockedBy) : JournalEntry
{
    /// <summary>The entry for a task that has just settled, as its snapshot shows it.</summary>
    public static TaskSettledEntry Of(NodeSnapshot settled, DateTimeOffset time)
        => new(
            settled.Path.ToString(),
            time,
            settled.Status,
            settled.Message,
            settled.Detail,
            settled.DetailOmitted,
            settled.Warnings,
            settled.WarningsOmitted,
            settled.Failure,
            settled.BlockedBy?.ToString());
}

/// <summary>A run of the journal ended with a run outcome.</summary>
/// <param name="Time">When the run ended.</param>
/// <param name="Outcome">How it ended.</param>
internal sealed record RunEndedEntry(DateTimeOffset Time, RunOutcome Outcome) : JournalEntry;
