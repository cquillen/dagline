// PROTOTYPE (throwaway, stubs only). Journals.
namespace Dagline;

/// <summary>The app's journal choice for a run. Dagline never picks one and has no default directory.</summary>
public abstract record Journal
{
    public static Journal New(string directory, string? description = null) => throw new NotImplementedException();
    public static Journal Continue(string directory, string id) => throw new NotImplementedException();
    public static Journal None => throw new NotImplementedException();
}

public sealed record JournalSummary(string Id, string? Description, DateTimeOffset Started, DateTimeOffset LastRun, int Runs, bool Finished, int Succeeded);

public static class Journals
{
    public static IReadOnlyList<JournalSummary> List(string directory) => throw new NotImplementedException();
    /// <summary>Refused with JournalInUseException while a run holds it.</summary>
    public static void Delete(string directory, string id) => throw new NotImplementedException();
}

public sealed class JournalInUseException : DaglineException { }
public sealed class JournalOpenException : DaglineException { }
public sealed class JournalFormatException : DaglineException { public int Version => throw new NotImplementedException(); }
public sealed class JournalNotFoundException : DaglineException { }

/// <summary>The run was stopped, waited out its grace period, and ended with this instead of a run outcome.</summary>
public sealed class JournalWriteException : DaglineException { public Snapshot Snapshot => throw new NotImplementedException(); }
