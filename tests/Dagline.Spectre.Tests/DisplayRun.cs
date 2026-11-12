using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Dagline.Spectre.Tests;

/// <summary>
/// A real run of a graph with the display forced on over a test console of a fixed size, sharing one fake clock with the run.
/// A frame test starts one, waits for its paused task bodies to reach the condition under test, and reads the next frame
/// as lines of text: <c>await using var display = await DisplayRun.StartAsync(graph, 100, 32);</c>, then
/// <c>await body.Started;</c> and <c>display.NextFrame()</c>.
/// </summary>
/// <remarks>
/// The console emits control sequences, and <see cref="Screen"/> reads them as a terminal would, so <see cref="LastFrame"/> is
/// what the alternate screen shows and <see cref="Summary"/> what the ordinary screen shows. Nothing waits on real time:
/// advancing the fake clock fires the display's timer on the calling thread, so the frame is drawn before
/// <see cref="NextFrame"/> reads it.
/// </remarks>
internal sealed class DisplayRun : IAsyncDisposable
{
    /// <summary>When every run of a display test starts.</summary>
    public static readonly DateTimeOffset RunStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    /// <summary>How often the display draws a frame: four times a second.</summary>
    public static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(250);

    private readonly WatchedReporter _reporter;
    private readonly HeldReporter? _held;
    private readonly Size _size;
    private readonly List<(int At, Size Size)> _resizes = [];

    private DisplayRun(FakeTimeProvider clock, TestConsole console, WatchedReporter reporter, HeldReporter? held, Task<RunResult> run)
    {
        Clock = clock;
        Console = console;
        _reporter = reporter;
        _held = held;
        _size = new Size(console.Profile.Width, console.Profile.Height);
        Run = run;
    }

    /// <summary>The clock of the run and of the display.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>The console the display draws on.</summary>
    public TestConsole Console { get; }

    /// <summary>The run, which completes with its result once it has ended and the display has handled every event.</summary>
    public Task<RunResult> Run { get; }

    /// <summary>What a terminal would show now, read from everything the display has written so far.</summary>
    public Screen Screen => Screen.Read(Console.Output, _size, _resizes);

    /// <summary>The last frame drawn, as lines of text: what the alternate screen shows now.</summary>
    public IReadOnlyList<string> LastFrame => Screen.Frame;

    /// <summary>What the ordinary screen shows: the summary, once the run has ended.</summary>
    public IReadOnlyList<string> Summary => Screen.Ordinary;

    /// <summary>Starts a run of <paramref name="graph"/> and returns once the display has handled <see cref="RunStarted"/>.</summary>
    /// <param name="graph">The graph to run, usually with task bodies the test pauses.</param>
    /// <param name="width">The width of the console in cells.</param>
    /// <param name="height">The height of the console in lines.</param>
    /// <param name="options">
    /// Options for the run, such as a journal or a cap. Their clock and reporters are replaced. The default runs with no journal.
    /// </param>
    /// <param name="stop">A stop signal for the run, if the test stops it.</param>
    /// <param name="holdBefore">
    /// An event, as <see cref="EventLine.Describe"/> writes it, that the display is not handed until <see cref="ReleaseHeld"/>,
    /// nor any event after it: the run moves on while the display falls behind.
    /// </param>
    /// <param name="ascii">Whether the display draws with the glyph set that uses only ASCII.</param>
    public static async Task<DisplayRun> StartAsync(
        Graph graph,
        int width,
        int height,
        RunOptions? options = null,
        StopSignal? stop = null,
        string? holdBefore = null,
        bool ascii = false)
    {
        var clock = new FakeTimeProvider(RunStart);
        var console = new TestConsole().Size(new Size(width, height)).EmitAnsiSequences();
        IReporter display = SpectreReporter.Auto(
            options: new SpectreOptions { Display = DisplayMode.Force, Ascii = ascii, Console = console, TimeProvider = clock });
        var held = holdBefore is null ? null : new HeldReporter(display, holdBefore);
        var reporter = new WatchedReporter(held ?? display);
        var runOptions = (options ?? new RunOptions { Journal = Journal.None }) with { TimeProvider = clock, Reporters = [reporter] };

        var run = Runner.RunAsync(graph, runOptions, stop);
        await reporter.Handled("run started");
        return new DisplayRun(clock, console, reporter, held, run);
    }

    /// <summary>
    /// Completes once the display has handled the event that <see cref="EventLine.Describe"/> writes as <paramref name="line"/>,
    /// such as <c>a: running -> succeeded</c>. The snapshot source shows that event from then on.
    /// </summary>
    public Task Handled(string line) => _reporter.Handled(line);

    /// <summary>Advances the clock by one redraw interval, which draws a frame, and returns that frame as lines of text.</summary>
    public IReadOnlyList<string> NextFrame()
    {
        Clock.Advance(RedrawInterval);
        return LastFrame;
    }

    /// <summary>Resizes the console, as an operator resizing the window between two frames.</summary>
    public void Resize(int width, int height)
    {
        _resizes.Add((Console.Output.Length, new Size(width, height)));
        Console.Profile.Width = width;
        Console.Profile.Height = height;
    }

    /// <summary>Hands the display the event it was held before, and every event after it.</summary>
    public void ReleaseHeld() => _held?.Release();

    /// <summary>Waits for the run to end and returns the summary the display left on the ordinary screen.</summary>
    public async Task<IReadOnlyList<string>> EndAsync()
    {
        await Run;
        return Summary;
    }

    /// <summary>Lets go of a held display, so that a run left going by a failed assertion can end.</summary>
    public ValueTask DisposeAsync()
    {
        ReleaseHeld();
        Console.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A reporter that hands events on to another and holds back one event, and every one after it, until released.</summary>
    private sealed class HeldReporter(IReporter inner, string holdBefore) : IReporter
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _holding;

        public void Release() => _released.TrySetResult();

        public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            _holding |= EventLine.Describe(runEvent) == holdBefore;
            if (_holding)
            {
                await _released.Task.WaitAsync(cancellationToken);
            }

            await inner.OnEventAsync(runEvent, snapshot, cancellationToken);
        }
    }
}
