using System.Runtime.InteropServices;
using System.Text;

namespace Dagline;

/// <summary>
/// Flushes a directory's entries to the disk, so that a file just created in it survives a crash of the operating system or a power cut.
/// </summary>
/// <remarks>
/// On Unix systems a new file's name lives in its directory, which a flush of the file does not reach, and .NET has no API for it:
/// the directory is opened and flushed with the system's own <c>open</c> and <c>fsync</c>. On Windows the file system records a new
/// file's name with the file, and nothing is done.
/// </remarks>
internal static class DirectoryEntries
{
    private const int _readOnly = 0;

    /// <summary>Flushes the entries of <paramref name="directory"/> to the disk, on a Unix system.</summary>
    /// <exception cref="IOException">The directory could not be opened or flushed.</exception>
    public static void Flush(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var descriptor = Open(Encoding.UTF8.GetBytes(Path.GetFullPath(directory) + '\0'), _readOnly);
        if (descriptor < 0)
        {
            throw Failed("opened", directory);
        }

        try
        {
            if (FileSync(descriptor) != 0)
            {
                throw Failed("flushed", directory);
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    private static IOException Failed(string done, string directory)
        => new($"The directory '{directory}' could not be {done} to record a new journal: error {Marshal.GetLastPInvokeError()}.");

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open(byte[] path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int FileSync(int descriptor);

    [DllImport("libc", EntryPoint = "close")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Close(int descriptor);
}
