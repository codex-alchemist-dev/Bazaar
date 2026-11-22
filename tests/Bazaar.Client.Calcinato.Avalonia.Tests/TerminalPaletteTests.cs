using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Client.Calcinato.Emulation;

namespace Bazaar.Client.Calcinato.Avalonia.Tests;

public class TerminalPaletteTests
{
    [Theory]
    [InlineData(16, 0, 0, 0)]
    [InlineData(21, 0, 0, 255)]
    [InlineData(196, 255, 0, 0)]
    [InlineData(231, 255, 255, 255)]
    [InlineData(232, 8, 8, 8)]
    [InlineData(244, 128, 128, 128)]
    [InlineData(255, 238, 238, 238)]
    public void Indexed_256_colours_are_computed_from_the_cube_and_ramp(byte index, byte r, byte g, byte b)
    {
        Color color = TerminalPalette.Indexed256(index);
        Assert.Equal(Color.FromRgb(r, g, b), color);
    }

    [Fact]
    public void Indexed_below_16_is_not_computed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalPalette.Indexed256(15));
    }

    [Fact]
    public void Base_colours_come_from_the_palette_and_256_colours_from_the_computed_table()
    {
        TerminalPalette palette = TerminalPalette.Fallback;
        Assert.Equal(palette.Ansi[1], palette.Resolve(TermColor.FromIndex(1), foreground: true));
        Assert.Equal(TerminalPalette.Indexed256(196), palette.Resolve(TermColor.FromIndex(196), foreground: true));
    }

    [Fact]
    public void Truecolour_passes_through_unchanged()
    {
        Color color = TerminalPalette.Fallback.Resolve(TermColor.FromRgb(12, 34, 56), foreground: false);
        Assert.Equal(Color.FromRgb(12, 34, 56), color);
    }

    [Fact]
    public void Default_colour_maps_to_the_foreground_or_background_by_role()
    {
        TerminalPalette palette = TerminalPalette.Fallback;
        Assert.Equal(palette.Foreground, palette.Resolve(TermColor.Default, foreground: true));
        Assert.Equal(palette.Background, palette.Resolve(TermColor.Default, foreground: false));
    }

    [AvaloniaFact]
    public void Missing_theme_tokens_fall_back_to_the_built_in_colours()
    {
        TerminalPalette palette = TerminalPalette.FromTheme();

        Assert.Equal(TerminalPalette.Fallback.Background, palette.Background);
        Assert.Equal(TerminalPalette.Fallback.Foreground, palette.Foreground);
        Assert.Equal(TerminalPalette.Fallback.Cursor, palette.Cursor);
        Assert.Equal(TerminalPalette.Fallback.Selection, palette.Selection);
        Assert.Equal(TerminalPalette.Fallback.Ansi[15], palette.Ansi[15]);
    }

    [AvaloniaFact]
    public void Theme_tokens_drive_the_palette_when_defined()
    {
        var app = Application.Current!;
        var keys = new[] { TerminalPalette.BackgroundToken, "Terminal.Ansi2", TerminalPalette.CursorToken };
        try
        {
            app.Resources[TerminalPalette.BackgroundToken] = Color.FromRgb(1, 2, 3);
            app.Resources["Terminal.Ansi2"] = new SolidColorBrush(Color.FromRgb(4, 5, 6));
            app.Resources[TerminalPalette.CursorToken] = Color.FromRgb(7, 8, 9);

            TerminalPalette palette = TerminalPalette.FromTheme();

            Assert.Equal(Color.FromRgb(1, 2, 3), palette.Background);
            Assert.Equal(Color.FromRgb(4, 5, 6), palette.Ansi[2]);
            Assert.Equal(Color.FromRgb(7, 8, 9), palette.Cursor);
            Assert.Equal(TerminalPalette.Fallback.Foreground, palette.Foreground);
        }
        finally
        {
            foreach (string key in keys)
            {
                app.Resources.Remove(key);
            }
        }
    }
}
