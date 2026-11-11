// PROTOTYPE for the ticket "Terminal UI: what the operator sees". Throwaway: never merge.
//
// Three structurally different layouts of one faked multi-hour run, switchable with the arrow keys,
// each drawable by two drivers (Spectre.Console's LiveDisplay, or our own loop writing Spectre-rendered lines).
// The real display is passive. Every key here belongs to the prototype, not to the design.
//
//   dotnet run --project samples/Dagline.Prototypes.TerminalUi
//   dotnet run --project samples/Dagline.Prototypes.TerminalUi -- --dump 120 40     (plain-text frames, no terminal needed)

using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace Dagline.Prototypes.TerminalUi;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0 && args[0] == "--dump")
        {
            Dump.Run(args);
            return 0;
        }

        if (args.Length > 1 && args[0] == "--smoke")
        {
            new App().Smoke(args[1]);
            return 0;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Run this in a real terminal, or pass --dump <width> <height> for plain-text frames.");
            return 1;
        }

        new App().Run();
        return 0;
    }
}

internal sealed record Frame(List<Line> Permanent, List<Line> Live);

internal sealed class App
{
    private const int GraphSeed = 7;
    private static readonly string[] s_names = [VariantTree.Name, VariantDashboard.Name, VariantFeed.Name];
    private static readonly int[] s_speeds = [1, 10, 60, 120, 300, 1200];
    private static readonly int[] s_refresh = [100, 250, 500, 1000];

    private readonly VariantFeed _feed = new();
    private readonly long _appStart = Stopwatch.GetTimestamp();
    private Sim _sim = new(GraphSeed);
    private Sim? _lastEnded;
    private int _scenario = 1;
    private int _variant;
    private bool _ownLoop;
    private bool _altScreen;
    private int _speed = 3;
    private int _refresh = 1;
    private bool _paused;
    private bool _hideBar;
    private bool _quit;
    private bool _restartSession;
    private bool _newRun;
    private int _stopPresses;
    private long _lastTick;
    private double _frameMs;
    private long _frames;
    private int _smokeLeft;

    /// <summary>Drives every driver, screen mode and layout for a few frames and logs whether each survived.</summary>
    public void Smoke(string log)
    {
        try
        {
            _speed = 4;
            _refresh = 0;
            foreach (var own in (bool[])[false, true])
            {
                foreach (var alt in (bool[])[false, true])
                {
                    for (var variant = 0; variant < 3; variant++)
                    {
                        (_ownLoop, _altScreen, _variant, _smokeLeft) = (own, alt, variant, 10);
                        _feed.Reset(_sim, fromStart: false);
                        _lastTick = Stopwatch.GetTimestamp();
                        if (own)
                        {
                            RunOwnLoop();
                        }
                        else
                        {
                            RunSpectreLive();
                        }

                        File.AppendAllText(log, $"ok own={own} alt={alt} variant={variant} frames={_frames} frameMs={_frameMs:0.0} size={Console.WindowWidth}x{Console.WindowHeight} sim={Fmt.Dur(_sim.Elapsed)}\n");
                    }
                }
            }

            _sim.RequestStop();
            Sim.RunToEnd(_sim);
            PrintSummary();
            File.AppendAllText(log, $"done outcome={_sim.Outcome}\n");
        }
        catch (Exception e)
        {
            File.AppendAllText(log, e + "\n");
        }
    }

    public void Run()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Interlocked.Increment(ref _stopPresses);
        };

        while (!_quit)
        {
            _sim = NewSim();
            _feed.Reset(_sim, fromStart: true);
            _newRun = false;
            while (!_quit && !_newRun && !_sim.Ended)
            {
                _restartSession = false;
                _lastTick = Stopwatch.GetTimestamp();
                if (_ownLoop)
                {
                    RunOwnLoop();
                }
                else
                {
                    RunSpectreLive();
                }
            }

            if (_sim.Ended && !_quit && !_newRun)
            {
                _lastEnded = _sim;
                PrintSummary();
                WaitForChoice();
            }
        }
    }

    private Sim NewSim()
    {
        if (_scenario == 1)
        {
            return new Sim(GraphSeed);
        }

        var first = _lastEnded is { Outcome: not "complete" } ? _lastEnded : Sim.RunToEnd(new Sim(GraphSeed));
        return new Sim(GraphSeed + first.RunNumber, first);
    }

    // ---------------------------------------------------------------- one frame

    private Frame? Tick(int width, int height)
    {
        HandleInput();
        if (_quit || _restartSession || _newRun || (_smokeLeft > 0 && --_smokeLeft == 0))
        {
            return null;
        }

        var now = Stopwatch.GetTimestamp();
        var real = Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        _lastTick = now;
        if (!_paused)
        {
            _sim.Advance(real * s_speeds[_speed]);
        }

        if (_sim.Ended)
        {
            return null;
        }

        width--;
        var usable = height - 1 - (_hideBar ? 0 : 2);
        List<Line> permanent = [];
        List<Line> live;
        switch (_variant)
        {
            case 0:
                live = VariantTree.Build(_sim, width, usable);
                break;
            case 1:
                live = VariantDashboard.Build(_sim, width, usable);
                break;
            default:
                permanent = _feed.Drain(_sim, width);
                live = VariantFeed.BuildFooter(_sim, width, usable);
                break;
        }

        if (!_hideBar)
        {
            live.AddRange(PrototypeBar(width, height));
        }

        return new Frame(permanent, live);
    }

    private List<Line> PrototypeBar(int width, int height)
    {
        var memory = GC.GetTotalMemory(false) / (1024 * 1024);
        var up = Fmt.Dur(Stopwatch.GetElapsedTime(_appStart));
        var first = new Line()
            .Add($" PROTOTYPE  < {"ABC"[_variant]}: {s_names[_variant]} > ", S.Bar)
            .Add($" {(_ownLoop ? "own loop" : "Spectre LiveDisplay")} | {(_altScreen ? "alt screen" : "inline")} | {(Glyphs.Unicode ? "unicode" : "ascii")}"
                + $" | x{s_speeds[_speed]}{(_paused ? " PAUSED" : "")} | every {s_refresh[_refresh]}ms | frame {_frameMs:0.0}ms"
                + $" | {width + 1}x{height} | {memory}MB | up {up} | {_frames} frames", S.BarDim);
        var second = new Line().Add(
            " arrows layout  d driver  a alt-screen  u glyphs  +/- speed  [ ] refresh  f +10m  F +1h  space pause  s or Ctrl+C stop  1 new run  2 continued run  h bar  q quit",
            S.Dim);
        return [first.Fit(width), second.Fit(width)];
    }

    private void HandleInput()
    {
        var presses = Interlocked.Exchange(ref _stopPresses, 0);
        for (var i = 0; i < presses; i++)
        {
            Stop();
        }

        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.LeftArrow)
            {
                SwitchVariant((_variant + 2) % 3);
                continue;
            }

            if (key.Key == ConsoleKey.RightArrow)
            {
                SwitchVariant((_variant + 1) % 3);
                continue;
            }

            switch (key.KeyChar)
            {
                case 'd':
                    _ownLoop = !_ownLoop;
                    _restartSession = true;
                    break;
                case 'a':
                    _altScreen = !_altScreen;
                    _restartSession = true;
                    break;
                case 'u':
                    Glyphs.Unicode = !Glyphs.Unicode;
                    break;
                case '+' or '=':
                    _speed = Math.Min(_speed + 1, s_speeds.Length - 1);
                    break;
                case '-' or '_':
                    _speed = Math.Max(_speed - 1, 0);
                    break;
                case ']':
                    _refresh = Math.Min(_refresh + 1, s_refresh.Length - 1);
                    break;
                case '[':
                    _refresh = Math.Max(_refresh - 1, 0);
                    break;
                case 'f':
                    _sim.Advance(600);
                    break;
                case 'F':
                    _sim.Advance(3600);
                    break;
                case ' ':
                    _paused = !_paused;
                    break;
                case 's':
                    Stop();
                    break;
                case '1' or '2':
                    _scenario = key.KeyChar - '0';
                    _newRun = true;
                    break;
                case 'h':
                    _hideBar = !_hideBar;
                    break;
                case 'q':
                    _quit = true;
                    break;
            }
        }
    }

    private void Stop()
    {
        _sim.RequestStop();
        _speed = 0;
        _paused = false;
    }

    private void SwitchVariant(int to)
    {
        _variant = to;
        _restartSession = true;
        if (to == 2)
        {
            _feed.Reset(_sim, fromStart: false);
        }
    }

    private static (int Width, int Height) Size() => (Math.Max(30, Console.WindowWidth), Math.Max(10, Console.WindowHeight));

    private void EndFrame(long started)
    {
        _frameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _frames++;
        Thread.Sleep(Math.Max(1, s_refresh[_refresh] - (int)_frameMs));
    }

    // ---------------------------------------------------------------- driver 1: Spectre.Console LiveDisplay

    private void RunSpectreLive()
    {
        var console = AnsiConsole.Console;

        void Body() => console.Live(new Text(" "))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Crop)
            .Cropping(VerticalOverflowCropping.Bottom)
            .Start(context =>
            {
                while (true)
                {
                    var started = Stopwatch.GetTimestamp();
                    var (width, height) = Size();
                    var frame = Tick(width, height);
                    if (frame is null)
                    {
                        return;
                    }

                    foreach (var line in frame.Permanent)
                    {
                        console.Write(line.ToRenderable());
                        console.WriteLine();
                    }

                    context.UpdateTarget(new Rows(frame.Live.Select(l => l.ToRenderable())));
                    EndFrame(started);
                }
            });

        if (_altScreen)
        {
            console.AlternateScreen(Body);
        }
        else
        {
            Body();
        }
    }

    // ---------------------------------------------------------------- driver 2: our own loop

    // Spectre.Console still turns styled lines into ANSI text, off screen. We place them ourselves:
    // cursor up to the top of the region, rewrite only the lines that changed, inside a synchronised update.
    private void RunOwnLoop()
    {
        const string Esc = "\u001b";
        var buffer = new StringWriter();
        var offscreen = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(buffer),
        });
        offscreen.Profile.Width = 1000;

        string Render(Line line)
        {
            buffer.GetStringBuilder().Clear();
            offscreen.Write(line.ToRenderable());
            return buffer.ToString().TrimEnd('\r', '\n');
        }

        var output = Console.Out;
        List<string> previous = [];
        var (lastWidth, lastHeight) = Size();
        output.Write((_altScreen ? $"{Esc}[?1049h{Esc}[H" : "") + $"{Esc}[?25l");
        try
        {
            while (true)
            {
                var started = Stopwatch.GetTimestamp();
                var (width, height) = Size();
                var frame = Tick(width, height);
                if (frame is null)
                {
                    return;
                }

                var sb = new StringBuilder($"{Esc}[?2026h");
                if (width != lastWidth || height != lastHeight)
                {
                    // A resize reflows what is on screen, so the old region can no longer be found: start clean.
                    sb.Append($"{Esc}[2J{Esc}[H");
                    previous = [];
                    (lastWidth, lastHeight) = (width, height);
                }

                if (previous.Count > 0)
                {
                    sb.Append($"{Esc}[{previous.Count}A");
                }

                sb.Append('\r');
                if (frame.Permanent.Count > 0)
                {
                    sb.Append($"{Esc}[J");
                    foreach (var line in frame.Permanent)
                    {
                        sb.Append(Render(line)).Append($"{Esc}[K\n");
                    }

                    previous = [];
                }

                var lines = frame.Live.Select(Render).ToList();
                for (var i = 0; i < lines.Count; i++)
                {
                    if (i < previous.Count && previous[i] == lines[i])
                    {
                        sb.Append($"{Esc}[1B");
                    }
                    else
                    {
                        sb.Append('\r').Append(lines[i]).Append($"{Esc}[K\n");
                    }
                }

                sb.Append('\r');
                if (lines.Count < previous.Count)
                {
                    sb.Append($"{Esc}[J");
                }

                sb.Append($"{Esc}[?2026l");
                output.Write(sb.ToString());
                output.Flush();
                previous = lines;
                EndFrame(started);
            }
        }
        finally
        {
            var clear = previous.Count > 0 ? $"{Esc}[{previous.Count}A\r{Esc}[J" : "";
            output.Write(clear + $"{Esc}[?25h" + (_altScreen ? $"{Esc}[?1049l" : ""));
            output.Flush();
        }
    }

    // ---------------------------------------------------------------- end of run

    private void PrintSummary()
    {
        var (width, _) = Size();
        if (_variant == 2)
        {
            foreach (var line in _feed.Drain(_sim, width - 1))
            {
                AnsiConsole.Write(line.ToRenderable());
                AnsiConsole.WriteLine();
            }
        }

        foreach (var line in Summary.Build(_sim, width - 1))
        {
            AnsiConsole.Write(line.ToRenderable());
            AnsiConsole.WriteLine();
        }
    }

    private void WaitForChoice()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Line().Add(" PROTOTYPE ", S.Bar).Add("  1 new run   2 run that continues this journal   q quit", S.BarDim).ToRenderable());
        AnsiConsole.WriteLine();
        while (true)
        {
            Interlocked.Exchange(ref _stopPresses, 0);
            var key = Console.ReadKey(intercept: true).KeyChar;
            if (key is '1' or '2')
            {
                _scenario = key - '0';
                return;
            }

            if (key == 'q')
            {
                _quit = true;
                return;
            }
        }
    }
}

/// <summary>Plain-text frames at chosen moments of the run, for reading without a terminal.</summary>
internal static class Dump
{
    public static void Run(string[] args)
    {
        var width = args.Length > 1 ? int.Parse(args[1]) : 120;
        var height = args.Length > 2 ? int.Parse(args[2]) : 40;
        var usable = height - 1;
        Glyphs.Unicode = !args.Contains("--ascii");

        var sim = new Sim(7);
        var feed = new VariantFeed();
        feed.Reset(sim, fromStart: true);

        void Show(string moment)
        {
            Print($"A: {VariantTree.Name}", moment, VariantTree.Build(sim, width, usable));
            Print($"B: {VariantDashboard.Name}", moment, VariantDashboard.Build(sim, width, usable));
            var scrolled = feed.Drain(sim, width);
            var footer = VariantFeed.BuildFooter(sim, width, usable);
            var room = Math.Max(0, usable - footer.Count);
            var tail = scrolled.Count > room ? scrolled[^room..] : scrolled;
            Print($"C: {VariantFeed.Name} ({scrolled.Count} new permanent lines, the last {tail.Count} shown)", moment, [.. tail, .. footer]);
        }

        void Print(string name, string moment, List<Line> lines)
        {
            Console.WriteLine();
            Console.WriteLine($"===== {name} | {moment} | {width}x{height} | {lines.Count} lines =====");
            foreach (var line in lines)
            {
                Console.WriteLine(line.Plain());
            }
        }

        foreach (var minutes in (int[])[2, 25, 40])
        {
            sim.Advance((minutes * 60) - sim.Elapsed.TotalSeconds);
            Show($"run 1 at +{minutes}m");
        }

        foreach (var minutes in (int[])[75, 130])
        {
            sim.Advance((minutes * 60) - sim.Elapsed.TotalSeconds);
            Show($"run 1 at +{minutes}m");
        }

        Sim.RunToEnd(sim);
        Print("Summary", "run 1 ended", Summary.Build(sim, width));

        var second = new Sim(8, sim);
        feed = new VariantFeed();
        feed.Reset(second, fromStart: true);
        sim = second;
        sim.Advance(20 * 60);
        Show("run 2 (continues the journal) at +20m");
        sim.Advance(40);
        sim.RequestStop();
        sim.Advance(4);
        Show("run 2, 4 seconds after a stop");
        Sim.RunToEnd(sim);
        Print("Summary", "run 2 stopped", Summary.Build(sim, width));
    }
}
