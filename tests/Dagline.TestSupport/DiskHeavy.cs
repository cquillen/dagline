namespace Dagline.TestSupport;

/// <summary>
/// The key of the tests in one test project that are heavy on the disk, which take it in turns:
/// <c>[NotInParallel(DiskHeavy.Key)]</c>.
/// </summary>
/// <remarks>
/// A test that repeats a run on a real journal 50 times flushes journal files to the disk hundreds of times, and one that starts
/// MSBuild processes reads a great many files. On a slow disk, such as a hosted Windows runner's, that work is bound by the disk
/// and not by the test, so when several such tests share the disk each ends only when nearly all of them have, and together
/// they pass the 30-second timeout. Taking turns, each ends after its own share.
/// </remarks>
public static class DiskHeavy
{
    /// <summary>The key passed to <c>NotInParallel</c>.</summary>
    public const string Key = "heavy on the disk";
}
