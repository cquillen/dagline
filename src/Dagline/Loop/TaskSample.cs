namespace Dagline;

/// <summary>
/// What a running task reports that only the latest value of matters (ADR 0015): its progress, and how many warnings and detail
/// lines it added beyond those kept. The task overwrites it from any of its threads, and whoever builds a snapshot reads it: the
/// loop as it publishes an event, and a reader of the run's current snapshot.
/// </summary>
/// <remarks>
/// <para>
/// It also decides, on the task's side, which warnings and detail lines are kept: the first <see cref="Kept"/> of each are posted
/// to the run as inputs, and the rest are only counted here, so a task that finds three thousand problems never floods the inbox.
/// </para>
/// <para>
/// A lock of its own, which nothing holds for long, keeps a count from being read with another report's total and keeps the task
/// safe from several threads. A version lets a sampler skip a task that has reported nothing since it last read. Once frozen, when
/// its task settles or is let go of, nothing changes it: every later reader sees what the loop read as the task settled.
/// </para>
/// </remarks>
internal sealed class TaskSample
{
    /// <summary>How many warnings, and separately how many detail lines, are kept for one task. The rest are counted.</summary>
    public const int Kept = 100;

    private readonly Lock _gate = new();
    private string? _statusText;
    private long? _count;
    private long? _total;
    private int _warningsKept;
    private int _detailKept;
    private int _warningsBeyondKept;
    private int _detailBeyondKept;
    private int _version;
    private bool _frozen;

    /// <summary>Changes each time the progress changes, so that a sampler that has read this version can skip the task.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>How many warnings the task added beyond those kept.</summary>
    public int WarningsBeyondKept => Volatile.Read(ref _warningsBeyondKept);

    /// <summary>How many detail lines the task added, with its detail or its reported failure, beyond those kept.</summary>
    public int DetailBeyondKept => Volatile.Read(ref _detailBeyondKept);

    /// <summary>Sets the status text, as given, or clears it when the text is empty.</summary>
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

    /// <summary>Sets the count, and the total it counts towards.</summary>
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

    /// <summary>Whether a warning being added is one of those kept, and so is to be posted. One beyond them is counted here.</summary>
    public bool KeepWarning()
    {
        lock (_gate)
        {
            if (_frozen)
            {
                return false;
            }

            if (_warningsKept < Kept)
            {
                _warningsKept++;
                return true;
            }

            _warningsBeyondKept++;
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

            var kept = Math.Min(lines, Kept - _detailKept);
            _detailKept += kept;
            _detailBeyondKept += lines - kept;
            return kept;
        }
    }

    /// <summary>The progress as it is now, read whole.</summary>
    public ProgressSample Read()
    {
        lock (_gate)
        {
            return new ProgressSample(_version, _statusText, _count, _total);
        }
    }

    /// <summary>Freezes the sample, so that nothing the task reports from now on changes it, and reads its progress whole.</summary>
    public ProgressSample Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
            return new ProgressSample(_version, _statusText, _count, _total);
        }
    }
}

/// <summary>A task's progress as read at one instant, with the version of its sample it was read at.</summary>
internal readonly record struct ProgressSample(int Version, string? StatusText, long? Count, long? Total);
