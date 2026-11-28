namespace Dagline.Prototype;

/// <summary>
/// What a task reports that is only ever sampled: its progress, and how many warnings and detail lines it added beyond those kept.
/// The task overwrites it; whoever builds a snapshot reads it (the loop as it publishes, and a reader of the current snapshot).
/// </summary>
/// <remarks>
/// Invented mechanisms, each needed to keep a sample consistent:
/// a lock of the task's own, so a count is never read with another report's total and the context stays safe from several threads;
/// a version, so the sampler skips a task that reported nothing; and a freeze, so that what the loop sampled when the task settled
/// (or was let go, with its body still writing) is what every later reader sees too.
/// </remarks>
internal sealed class TaskSlot
{
    public const int Kept = 100;

    private readonly Lock _gate = new();
    private string? _statusText;
    private long? _count;
    private long? _total;
    private int _warningsAdded;
    private int _detailAdded;
    private int _warningsBeyondKept;
    private int _detailBeyondKept;
    private int _version;
    private bool _frozen;

    public void SetStatusText(string? text)
    {
        lock (_gate)
        {
            if (_frozen)
            {
                return;
            }

            _statusText = string.IsNullOrEmpty(text) ? null : text;
            _version++;
        }
    }

    public void SetCount(long current, long? total)
    {
        lock (_gate)
        {
            if (_frozen)
            {
                return;
            }

            _count = current;
            _total = total;
            _version++;
        }
    }

    /// <summary>Whether the warning being added is one of those kept, and so is to be posted. One beyond them is counted here.</summary>
    public bool TryKeepWarning()
    {
        lock (_gate)
        {
            if (_frozen)
            {
                return false;
            }

            if (_warningsAdded < Kept)
            {
                _warningsAdded++;
                return true;
            }

            _warningsBeyondKept++;
            _version++;
            return false;
        }
    }

    /// <summary>How many of <paramref name="lines"/> detail lines being added are kept, and so are to be posted. The rest are counted here.</summary>
    public int KeepDetail(int lines)
    {
        lock (_gate)
        {
            if (_frozen)
            {
                return 0;
            }

            var kept = Math.Min(lines, Kept - _detailAdded);
            _detailAdded += kept;
            if (lines > kept)
            {
                _detailBeyondKept += lines - kept;
                _version++;
            }

            return kept;
        }
    }

    /// <summary>
    /// Reads the slot whole, if it changed since <paramref name="sample"/> was taken or is to be frozen.
    /// </summary>
    /// <returns>Whether <paramref name="sample"/> changed.</returns>
    public bool Read(ref SlotSample sample, bool freeze = false)
    {
        if (!freeze && Volatile.Read(ref _version) == sample.Version)
        {
            return false;
        }

        lock (_gate)
        {
            _frozen |= freeze;
            if (_version == sample.Version)
            {
                return false;
            }

            sample = new SlotSample(_version, _statusText, _count, _total, _warningsBeyondKept, _detailBeyondKept);
            return true;
        }
    }
}

internal readonly record struct SlotSample(
    int Version,
    string? StatusText,
    long? Count,
    long? Total,
    int WarningsBeyondKept,
    int DetailBeyondKept);
