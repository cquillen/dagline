namespace Dagline;

/// <summary>The one answer to whether an exception is the file system refusing an operation on a journal, its lock file or its directory.</summary>
internal static class FileSystemFailure
{
    /// <summary>
    /// Whether <paramref name="exception"/> is a failure of the file system: an <see cref="IOException"/>, such as a full disk or a
    /// file that is gone, or an <see cref="UnauthorizedAccessException"/>, a permission the process lacks or a read-only file.
    /// </summary>
    public static bool Is(Exception exception) => exception is IOException or UnauthorizedAccessException;
}
