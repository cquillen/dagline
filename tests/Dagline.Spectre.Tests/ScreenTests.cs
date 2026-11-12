using Spectre.Console;

namespace Dagline.Spectre.Tests;

// The emulator the frame tests read frames with: it must measure text in cells as a terminal does, or it would miss a frame
// that overflows, or see an overflow in one that fits.
public class ScreenTests
{
    private const string _alternate = "\u001b[?1049h";

    [Test]
    public void A_wide_character_takes_two_cells()
    {
        var screen = Screen.Read(_alternate + "界界A", new Size(4, 2));

        screen.Overflowed.ShouldBeTrue();
    }

    [Test]
    public void Wide_characters_that_fit_do_not_overflow_and_read_back_as_written()
    {
        var screen = Screen.Read(_alternate + "界界", new Size(4, 2));

        screen.Overflowed.ShouldBeFalse();
        screen.Frame.ShouldBe(["界界"]);
    }

    [Test]
    public void A_combining_mark_takes_no_cell_of_its_own()
    {
        var screen = Screen.Read(_alternate + "café", new Size(4, 2));

        screen.Overflowed.ShouldBeFalse();
        screen.Frame.ShouldBe(["café"]);
    }

    [Test]
    public void A_cell_after_a_wide_character_is_where_a_terminal_puts_it()
    {
        var screen = Screen.Read(_alternate + "界\u001b[38;5;2mA", new Size(4, 2));

        screen.StyleAt(0, 2).ShouldBe("38;5;2");
    }
}
