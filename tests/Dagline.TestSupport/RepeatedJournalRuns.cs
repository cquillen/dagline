namespace Dagline.TestSupport;

/// <summary>
/// The key of the tests that repeat a run on a real journal, which take the disk in turns:
/// <c>[NotInParallel(RepeatedJournalRuns.Key)]</c>.
/// </summary>
/// <remarks>
/// Every repeat writes and flushes several journal files to the disk. On a slow disk, such as a hosted Windows runner's, that
/// work is bound by the disk and not by the test, so when these tests share the disk each one ends only when nearly all of them
/// have, and together they pass the 30-second timeout. Taking turns, each one ends after its own share.
/// </remarks>
public static class RepeatedJournalRuns
{
    /// <summary>The key passed to <c>NotInParallel</c>.</summary>
    public const string Key = "repeated runs on real journals";
}
