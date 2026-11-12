namespace Dagline;

/// <summary>
/// The hold on one journal: its lock file, <c>&lt;id&gt;.lock</c> beside the journal, open for exclusive use. A run takes it before
/// it opens the journal and lets go of it when the run ends.
/// </summary>
/// <remarks>
/// <para>
/// The lock file is opened with <see cref="FileShare.None"/>, which Windows enforces and which .NET maps to an advisory
/// <c>flock</c> elsewhere. Either way a second open fails while the first is held, in this process or another.
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

    /// <summary>Holds the journal <paramref name="id"/> in <paramref name="directory"/> until disposed, creating its lock file if missing.</summary>
    /// <exception cref="JournalInUseException">Another run holds the journal.</exception>
    /// <exception cref="JournalOpenException">The lock file cannot be opened or created.</exception>
    public static JournalLock Hold(string directory, string id) => new(Open(directory, id, FileMode.OpenOrCreate, FileOptions.None));

    /// <summary>
    /// Holds the journal <paramref name="id"/> in <paramref name="directory"/> so that it can be deleted: the lock file goes when
    /// this is disposed.
    /// </summary>
    /// <exception cref="JournalInUseException">Another run holds the journal.</exception>
    /// <exception cref="JournalOpenException">The lock file cannot be opened or created.</exception>
    public static JournalLock HoldToDelete(string directory, string id)
        => new(Open(directory, id, FileMode.OpenOrCreate, FileOptions.DeleteOnClose));

    /// <summary>
    /// Whether a run holds the journal <paramref name="id"/> in <paramref name="directory"/> at this instant. A journal with no lock
    /// file is not held. Asking holds the lock for a moment, without creating it.
    /// </summary>
    public static bool IsHeld(string directory, string id)
    {
        try
        {
            Open(directory, id, FileMode.Open, FileOptions.None).Dispose();
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

    private static FileStream Open(string directory, string id, FileMode mode, FileOptions options)
    {
        var path = JournalId.LockIn(directory, id);
        try
        {
            return new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None, bufferSize: 0, options);
        }
        catch (IOException held) when (IsHeldElsewhere(held))
        {
            throw new JournalInUseException(directory, id, held);
        }
        catch (Exception cannotOpen) when (cannotOpen is IOException or UnauthorizedAccessException)
        {
            throw new JournalOpenException(path, cannotOpen);
        }
    }

    // Windows reports a file another handle holds as a sharing or lock violation. Elsewhere .NET reports a failed flock as a plain
    // IOException, while every other failure to open is a subtype or carries another code.
    private static bool IsHeldElsewhere(IOException failure)
        => OperatingSystem.IsWindows()
            ? (failure.HResult & 0xFFFF) is _sharingViolation or _lockViolation
            : failure.GetType() == typeof(IOException);
}
