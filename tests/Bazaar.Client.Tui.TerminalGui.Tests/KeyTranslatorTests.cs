using Terminal.Gui.Input;

namespace Bazaar.Client.Tui.TerminalGui.Tests;

public class KeyTranslatorTests
{
    [Theory]
    [InlineData(TerminalKeyKind.Up)]
    [InlineData(TerminalKeyKind.Down)]
    [InlineData(TerminalKeyKind.Left)]
    [InlineData(TerminalKeyKind.Right)]
    [InlineData(TerminalKeyKind.Enter)]
    [InlineData(TerminalKeyKind.Escape)]
    [InlineData(TerminalKeyKind.Backspace)]
    [InlineData(TerminalKeyKind.Tab)]
    public void Navigation_keys_map_to_the_port_kinds(TerminalKeyKind kind)
    {
        var key = kind switch
        {
            TerminalKeyKind.Up => Key.CursorUp,
            TerminalKeyKind.Down => Key.CursorDown,
            TerminalKeyKind.Left => Key.CursorLeft,
            TerminalKeyKind.Right => Key.CursorRight,
            TerminalKeyKind.Enter => Key.Enter,
            TerminalKeyKind.Escape => Key.Esc,
            TerminalKeyKind.Backspace => Key.Backspace,
            _ => Key.Tab,
        };

        Assert.Equal(kind, KeyTranslator.Translate(key)?.Kind);
    }

    [Fact]
    public void A_printable_key_is_a_character_with_its_value()
    {
        Assert.Equal(TerminalKey.Of('k'), KeyTranslator.Translate(new Key('k')));
        Assert.Equal(TerminalKey.Of('/'), KeyTranslator.Translate(new Key('/')));
        Assert.Equal(TerminalKey.Of('K'), KeyTranslator.Translate(new Key('K')));
    }

    [Fact]
    public void Ctrl_c_is_cancel_and_ctrl_d_is_end_of_input()
    {
        Assert.Equal(TerminalKeyKind.Cancel, KeyTranslator.Translate(Key.C.WithCtrl)?.Kind);
        Assert.Equal(TerminalKeyKind.EndOfInput, KeyTranslator.Translate(Key.D.WithCtrl)?.Kind);
    }

    [Fact]
    public void Other_ctrl_and_alt_chords_are_ignored()
    {
        Assert.Null(KeyTranslator.Translate(Key.L.WithCtrl));
        Assert.Null(KeyTranslator.Translate(Key.K.WithAlt));
        Assert.Null(KeyTranslator.Translate(Key.F5));
    }
}
