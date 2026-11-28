namespace Dagline.TestSupport;

/// <summary>
/// The key of the tests in one test project that are heavy on the disk, which take it in turns:
/// <c>[NotInParallel(DiskHeavy.Key)]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A test that flushes journal files to the disk hundreds of times, such as one that continues every cut of a journal flushed to
/// the disk, or one that starts MSBuild processes, which read a great many files, is bound by the disk and not by the test on a
/// slow disk such as a hosted Windows runner's. When several such tests share the disk each ends only when nearly all of them
/// have, and together they pass the 30-second timeout. Taking turns, each ends after its own share.
/// </para>
/// <para>
/// A test that repeats runs on a real journal but is not about what survives a power cut uses <see cref="QuickJournal"/>, which
/// makes no flush, and needs no mark.
/// </para>
/// </remarks>
public static class DiskHeavy
{
    /// <summary>The key passed to <c>NotInParallel</c>.</summary>
    public const string Key = "heavy on the disk";
}
