using System.Text;
using Spectre.Console;

namespace Dagline.Spectre.Tests;

/// <summary>
/// What a terminal would show after it had been sent a test console's output: a small emulator that understands the
/// control sequences the display writes. It keeps the alternate screen as a grid of cells and the ordinary screen as the
/// lines written to it, and it records every mode the output switched, so a test can assert on the last frame as text,
/// on the summary, and on the control sequences pairing up.
/// </summary>
/// <remarks>
/// Read the output of a <c>TestConsole</c> made with <c>EmitAnsiSequences()</c>: without it the console drops every
/// control sequence and the frames run together. Sequences the emulator does not know are skipped.
/// </remarks>
internal sealed class Screen
{
    private const char _escape = '\u001b';

    private Cell[][] _alternate;
    private readonly List<StringBuilder> _ordinary = [new()];
    private readonly List<string> _modes = [];
    private int _width;
    private int _height;
    private int _row;
    private int _column;
    private bool _pendingWrap;
    private string _style = "";

    private Screen(Size size)
    {
        _width = size.Width;
        _height = size.Height;
        _alternate = Blank(_width, _height);
    }

    /// <summary>Whether the output left the terminal on the alternate screen.</summary>
    public bool OnAlternateScreen { get; private set; }

    /// <summary>Whether the cursor is visible at the end of the output.</summary>
    public bool CursorVisible { get; private set; } = true;

    /// <summary>How many frames the output drew: each is wrapped in one synchronised update.</summary>
    public int Frames { get; private set; }

    /// <summary>
    /// Whether any character was written past the right edge of the alternate screen, or below its foot,
    /// which a frame that fits the console never does.
    /// </summary>
    public bool Overflowed { get; private set; }

    /// <summary>
    /// The private modes the output set and reset, in order, such as <c>?1049h</c> (enter the alternate screen),
    /// <c>?25l</c> (hide the cursor), <c>?25h</c> and <c>?1049l</c>. Synchronised updates (<c>?2026</c>) are left out.
    /// </summary>
    public IReadOnlyList<string> Modes => _modes;

    /// <summary>
    /// The alternate screen as lines of text: the last frame drawn, with trailing spaces and trailing empty lines removed.
    /// </summary>
    public IReadOnlyList<string> Frame
    {
        get
        {
            var lines = _alternate.Select(row => new string([.. row.Select(cell => cell.Character)]).TrimEnd()).ToList();
            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }
    }

    /// <summary>The lines written to the ordinary screen, such as the summary, with a trailing empty line removed.</summary>
    public IReadOnlyList<string> Ordinary
    {
        get
        {
            var lines = _ordinary.Select(line => line.ToString()).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }
    }

    /// <summary>
    /// The parameters of the SGR sequence in force when the cell of the alternate screen at <paramref name="row"/> and
    /// <paramref name="column"/> (both from zero) was written, such as <c>38;5;2</c>, or empty for the default style.
    /// </summary>
    public string StyleAt(int row, int column) => _alternate[row][column].Style;

    /// <summary>Reads a console's output as a terminal of <paramref name="size"/> would show it.</summary>
    /// <param name="output">Everything the console was sent.</param>
    /// <param name="size">The size of the terminal when the output began.</param>
    /// <param name="resizes">
    /// Where the terminal was resized: the length the output had reached at that moment, and the new size.
    /// </param>
    public static Screen Read(string output, Size size, IReadOnlyList<(int At, Size Size)>? resizes = null)
    {
        var screen = new Screen(size);
        var start = 0;
        foreach (var (at, newSize) in resizes ?? [])
        {
            screen.Feed(output[start..at]);
            screen.Resize(newSize);
            start = at;
        }

        screen.Feed(output[start..]);
        return screen;
    }

    private void Feed(string output)
    {
        var index = 0;
        while (index < output.Length)
        {
            var character = output[index];
            if (character == _escape && index + 1 < output.Length && output[index + 1] == '[')
            {
                var end = index + 2;
                while (end < output.Length && output[end] is >= '0' and <= '?')
                {
                    end++;
                }

                Control(output[(index + 2)..end], output[end]);
                index = end + 1;
                continue;
            }

            if (character == _escape)
            {
                index += 2;
                continue;
            }

            Print(character);
            index++;
        }
    }

    private void Control(string parameters, char final)
    {
        switch (final)
        {
            case 'm':
                _style = parameters is "0" or "" ? "" : parameters;
                break;
            case 'H':
                var position = parameters.Split(';');
                _row = Number(position, 0) - 1;
                _column = Number(position, 1) - 1;
                _pendingWrap = false;
                break;
            case 'K' when OnAlternateScreen && _row < _height:
                EraseInLine(Number([parameters], 0, fallback: 0));
                break;
            case 'J' when OnAlternateScreen:
                EraseInDisplay(Number([parameters], 0, fallback: 0));
                break;
            case 'h' or 'l' when parameters.StartsWith('?'):
                Mode(parameters, final == 'h');
                break;
        }
    }

    private void Mode(string parameters, bool set)
    {
        if (parameters == "?2026")
        {
            Frames += set ? 1 : 0;
            return;
        }

        _modes.Add(parameters + (set ? 'h' : 'l'));
        switch (parameters)
        {
            case "?1049":
                OnAlternateScreen = set;
                if (set)
                {
                    _alternate = Blank(_width, _height);
                    _row = 0;
                    _column = 0;
                }

                break;
            case "?25":
                CursorVisible = set;
                break;
        }
    }

    private void Print(char character)
    {
        if (!OnAlternateScreen)
        {
            if (character == '\n')
            {
                _ordinary.Add(new());
            }
            else if (character != '\r')
            {
                _ordinary[^1].Append(character);
            }

            return;
        }

        switch (character)
        {
            case '\r':
                _column = 0;
                _pendingWrap = false;
                return;
            case '\n':
                NextRow();
                return;
        }

        if (_pendingWrap)
        {
            Overflowed = true;
            _column = 0;
            NextRow();
        }

        if (_row >= _height || _column >= _width)
        {
            Overflowed = true;
            return;
        }

        _alternate[_row][_column] = new Cell(character, _style);
        _column++;
        if (_column == _width)
        {
            _column = _width - 1;
            _pendingWrap = true;
        }
    }

    private void NextRow()
    {
        _pendingWrap = false;
        if (_row < _height - 1)
        {
            _row++;
            return;
        }

        _alternate = [.. _alternate.Skip(1), BlankRow(_width)];
    }

    private void EraseInLine(int mode)
    {
        var (from, to) = mode switch
        {
            1 => (0, _column + 1),
            2 => (0, _width),
            _ => (_column, _width),
        };

        for (var column = from; column < to && column < _width; column++)
        {
            _alternate[_row][column] = Cell.Blank;
        }
    }

    private void EraseInDisplay(int mode)
    {
        if (mode == 2)
        {
            _alternate = Blank(_width, _height);
            return;
        }

        if (_row < _height)
        {
            EraseInLine(0);
        }

        for (var row = _row + 1; row < _height; row++)
        {
            _alternate[row] = BlankRow(_width);
        }
    }

    private void Resize(Size size)
    {
        // As a terminal does on its alternate screen: what fits stays, and nothing reflows.
        var resized = Blank(size.Width, size.Height);
        for (var row = 0; row < Math.Min(_height, size.Height); row++)
        {
            Array.Copy(_alternate[row], resized[row], Math.Min(_width, size.Width));
        }

        _alternate = resized;
        _width = size.Width;
        _height = size.Height;
        _row = Math.Min(_row, _height - 1);
        _column = Math.Min(_column, _width - 1);
        _pendingWrap = false;
    }

    private static int Number(string[] parts, int index, int fallback = 1)
        => index < parts.Length && int.TryParse(parts[index], out var number) ? number : fallback;

    private static Cell[][] Blank(int width, int height) => [.. Enumerable.Range(0, height).Select(_ => BlankRow(width))];

    private static Cell[] BlankRow(int width) => [.. Enumerable.Repeat(Cell.Blank, width)];

    private readonly record struct Cell(char Character, string Style)
    {
        public static Cell Blank { get; } = new(' ', "");
    }
}
