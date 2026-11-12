namespace Dagline;

/// <summary>
/// The claim on one journal: its lock file, <c>&lt;id&gt;.lock</c> beside the journal, open for exclusive use. A run takes it before
/// it opens the journal and lets go of it when the run ends.
/// </summary>
/// <remarks>
/// <para>
/// The lock file is opened with <see cref="FileShare.None"/>, which Windows enforces and which .NET maps to an advisory
/// <c>flock</c> elsewhere. Either way a second open fails while the first is open, in this process or another.
/// </para>
/// <para>
/// A run leaves the lock file behind when it lets go. Deleting it then could race with a run that had opened it a moment
/// before, and would leave two runs each holding a lock of its own. A lock file that nothing holds is harmless, and
/// <see cref="Journals.DeleteAsync"/> removes it with its journal.
/// </para>
/// </remarks>
internal sealed class JournalLock : IDisposable
{
    private const int _sharingViolation = 32;
    private const int _lockViolation = 33;

    private readonly FileStream _file;

    private JournalLock(FileStream file) => _file = file;

    /// <summary>Claims the journal at <paramref name="location"/> until disposed, creating its lock file if missing.</summary>
    /// <exception cref="JournalInUseException">Another run has claimed the journal.</exception>
    /// <exception cref="JournalOpenException">The lock file cannot be opened or created.</exception>
    public static JournalLock Claim(JournalLocation location) => new(Open(location, FileMode.OpenOrCreate, FileOptions.None));

    /// <summary>
    /// Claims the journal at <paramref name="location"/> so that it can be deleted: the lock file goes when this is disposed.
    /// </summary>
    /// <exception cref="JournalInUseException">Another run has claimed the journal.</exception>
    /// <exception cref="JournalOpenException">The lock file cannot be opened or created.</exception>
    public static JournalLock ClaimToDelete(JournalLocation location)
        => new(Open(location, FileMode.OpenOrCreate, FileOptions.DeleteOnClose));

    /// <summary>
    /// Whether a run has claimed the journal at <paramref name="location"/> at this instant. A journal with no lock file is not in use.
    /// Asking claims the journal for a moment, without creating its lock file.
    /// </summary>
    public static bool IsInUse(JournalLocation location)
    {
        try
        {
            Open(location, FileMode.Open, FileOptions.None).Dispose();
            return false;
        }
        catch (JournalInUseException)
        {
            return true;
        }
        catch (JournalOpenException cannotOpen) when (cannotOpen.InnerException is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Lets go of the journal.</summary>
    public void Dispose() => _file.Dispose();

    private static FileStream Open(JournalLocation location, FileMode mode, FileOptions options)
    {
        try
        {
            return new FileStream(location.LockPath, mode, FileAccess.ReadWrite, FileShare.None, bufferSize: 0, options);
        }
        catch (IOException inUse) when (IsClaimedElsewhere(inUse))
        {
            throw new JournalInUseException(location, inUse);
        }
        catch (Exception cannotOpen) when (FileSystemFailure.Is(cannotOpen))
        {
            throw new JournalOpenException(location.LockPath, cannotOpen);
        }
    }

    // Windows reports a file another handle has open as a sharing or lock violation. Elsewhere .NET reports a failed flock as a plain
    // IOException, while every other failure to open is a subtype or carries another code.
    private static bool IsClaimedElsewhere(IOException failure)
        => OperatingSystem.IsWindows()
            ? (failure.HResult & 0xFFFF) is _sharingViolation or _lockViolation
            : failure.GetType() == typeof(IOException);
}
