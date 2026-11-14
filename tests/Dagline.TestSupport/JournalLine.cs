using System.Text.Json;

namespace Dagline.TestSupport;

/// <summary>
/// What the shared test support reads from one line of a journal: the kind of its entry and, for an entry about one task, the
/// task's path. The only code outside the core that knows the journal's line format.
/// </summary>
/// <param name="Kind">The kind of the entry, as the line names it.</param>
/// <param name="Path">The task's path as text, or <see langword="null"/> for an entry about the whole journal or run.</param>
internal readonly record struct JournalLine(string Kind, string? Path)
{
    private const string _settle = "taskSettled";
    private const string _checkpointSave = "checkpointSaved";
    private const string _runEnd = "runEnded";

    /// <summary>The line of the run's end.</summary>
    public static JournalLine RunEnd { get; } = new(_runEnd, null);

    /// <summary>The line of the settle of the task at <paramref name="path"/>.</summary>
    public static JournalLine SettleOf(string path) => new(_settle, path);

    /// <summary>The line of a checkpoint save of the task at <paramref name="path"/>.</summary>
    public static JournalLine CheckpointSaveOf(string path) => new(_checkpointSave, path);

    /// <summary>Reads one whole line of a journal, without its line feed.</summary>
    /// <exception cref="JsonException">The line is not a JSON object.</exception>
    public static JournalLine Of(string line)
    {
        using var entry = JsonDocument.Parse(line);
        var root = entry.RootElement;
        var path = root.TryGetProperty("path", out var element) ? element.GetString() : null;
        return new JournalLine(root.GetProperty("entry").GetString()!, path);
    }

    /// <summary>
    /// The entry in words, free of the line format: <c>header</c>, <c>run started</c>, <c>started load</c>, <c>settled load</c>,
    /// <c>set aside load</c>, <c>checkpoint saved load</c>, <c>checkpoint cleared load</c> or <c>run ended</c>. A kind this does
    /// not know is written as the line names it, then the path if there is one.
    /// </summary>
    public string Describe() => Kind switch
    {
        "header" => "header",
        "runStarted" => "run started",
        "taskStarted" => $"started {Path}",
        _settle => $"settled {Path}",
        "setAside" => $"set aside {Path}",
        _checkpointSave => $"checkpoint saved {Path}",
        "checkpointCleared" => $"checkpoint cleared {Path}",
        _runEnd => "run ended",
        _ => Path is null ? Kind : $"{Kind} {Path}",
    };
}
