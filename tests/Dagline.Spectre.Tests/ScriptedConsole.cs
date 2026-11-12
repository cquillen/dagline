using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace Dagline.Spectre.Tests;

/// <summary>
/// A test console a test can script: it throws from the next write once armed with <see cref="FailNextWrite"/>, as an app's
/// console might part of the way through a run, and tells a test when the next write has been made. Every write that does not
/// fail goes to the test console beneath it.
/// </summary>
internal sealed class ScriptedConsole(TestConsole inner) : IAnsiConsole, IDisposable
{
    private readonly Lock _gate = new();
    private bool _failNext;
    private TaskCompletionSource? _nextWrite;

    /// <summary>The console beneath, which holds everything written so far.</summary>
    public TestConsole Inner => inner;

    public Profile Profile => inner.Profile;

    public IAnsiConsoleCursor Cursor => inner.Cursor;

    public IAnsiConsoleInput Input => inner.Input;

    public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;

    public RenderPipeline Pipeline => inner.Pipeline;

    /// <summary>Makes the next write throw.</summary>
    public void FailNextWrite()
    {
        lock (_gate)
        {
            _failNext = true;
        }
    }

    /// <summary>Completes once the next write has been made to the console beneath.</summary>
    public Task NextWrite()
    {
        lock (_gate)
        {
            _nextWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _nextWrite.Task;
        }
    }

    public void Clear(bool home) => inner.Clear(home);

    public void Write(IRenderable renderable)
    {
        TaskCompletionSource? written;
        lock (_gate)
        {
            if (_failNext)
            {
                _failNext = false;
                throw new IOException("the console is gone");
            }

            inner.Write(renderable);
            written = _nextWrite;
            _nextWrite = null;
        }

        written?.TrySetResult();
    }

    public void WriteAnsi(Action<AnsiWriter> action) => inner.WriteAnsi(action);

    public void Dispose() => inner.Dispose();
}
