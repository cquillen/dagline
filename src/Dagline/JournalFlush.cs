namespace Dagline;

/// <summary>Whether an append to a journal is flushed to the disk before it returns.</summary>
internal enum JournalFlush
{
    /// <summary>The append reaches the operating system in one write, and is not flushed: a killed process keeps it, a power cut may not.</summary>
    None,

    /// <summary>The append is flushed to the disk before it returns, and survives a power cut.</summary>
    ToDisk,
}
