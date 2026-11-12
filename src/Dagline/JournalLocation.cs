using System.Runtime.CompilerServices;

namespace Dagline;

/// <summary>
/// Where one journal is: its directory and its id, which is always in the id format, so that its paths never name a file outside
/// the directory. The one home of the journal's file and lock file paths.
/// </summary>
internal sealed record JournalLocation
{
    private const string _lockExtension = ".lock";

    private JournalLocation(string directory, string id)
    {
        Directory = directory;
        Id = id;
    }

    /// <summary>The directory the journal is in.</summary>
    public string Directory { get; }

    /// <summary>The journal's id.</summary>
    public string Id { get; }

    /// <summary>The path of the journal's file, <c>&lt;id&gt;.journal</c> in its directory.</summary>
    public string FilePath => Path.Combine(Directory, Id + JournalId.Extension);

    /// <summary>The path of the journal's lock file, <c>&lt;id&gt;.lock</c> beside its file.</summary>
    public string LockPath => Path.Combine(Directory, Id + _lockExtension);

    /// <summary>The journal <paramref name="id"/> in <paramref name="directory"/>, refusing an id given at a public call that is not in the id format.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not in the id format.</exception>
    public static JournalLocation Of(string directory, string id, [CallerArgumentExpression(nameof(id))] string? paramName = null)
    {
        JournalId.ThrowIfNotValid(id, paramName);

        return new JournalLocation(directory, id);
    }

    /// <summary>A new journal in <paramref name="directory"/> created at <paramref name="created"/>, with a newly drawn id.</summary>
    public static JournalLocation New(string directory, DateTimeOffset created) => new(directory, JournalId.New(created));

    /// <summary>Refuses a journal whose file is not there.</summary>
    /// <exception cref="JournalNotFoundException">The directory has no journal with the id.</exception>
    public void ThrowIfNotFound()
    {
        if (!File.Exists(FilePath))
        {
            throw new JournalNotFoundException(this, new FileNotFoundException($"Could not find file '{FilePath}'.", FilePath));
        }
    }
}
