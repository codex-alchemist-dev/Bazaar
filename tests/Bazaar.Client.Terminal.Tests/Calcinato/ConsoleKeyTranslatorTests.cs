using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Terminal.Calcinato;

namespace Bazaar.Client.Terminal.Tests.Calcinato;

public sealed class ConsoleKeyTranslatorTests
{
    public static TheoryData<ConsoleKeyInfo, TerminalKey?> Cases => new()
    {
        { new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), TerminalKey.Named(KeyName.Enter) },
        { new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false), TerminalKey.Named(KeyName.Tab) },
        { new ConsoleKeyInfo('\t', ConsoleKey.Tab, true, false, false), TerminalKey.Named(KeyName.Tab, shift: true) },
        { new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false), TerminalKey.Named(KeyName.Backspace) },
        { new ConsoleKeyInfo('\u007F', ConsoleKey.NoName, false, false, false), TerminalKey.Named(KeyName.Backspace) },
        { new ConsoleKeyInfo('\u001B', ConsoleKey.Escape, false, false, false), TerminalKey.Named(KeyName.Escape) },
        { new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false), TerminalKey.Named(KeyName.Up) },
        { new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false), TerminalKey.Named(KeyName.Left) },
        { new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false), TerminalKey.Named(KeyName.Right) },
        { new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), TerminalKey.Named(KeyName.Down) },
        { new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false), TerminalKey.Named(KeyName.Home) },
        { new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false), TerminalKey.Named(KeyName.End) },
        { new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false), TerminalKey.Named(KeyName.PageUp) },
        { new ConsoleKeyInfo('\0', ConsoleKey.PageDown, true, false, false), TerminalKey.Named(KeyName.PageDown, shift: true) },
        { new ConsoleKeyInfo('\0', ConsoleKey.Insert, false, false, false), TerminalKey.Named(KeyName.Insert) },
        { new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false), TerminalKey.Named(KeyName.Delete) },
        { new ConsoleKeyInfo('\0', ConsoleKey.F1, false, false, false), TerminalKey.Named(KeyName.F1) },
        { new ConsoleKeyInfo('\0', ConsoleKey.F5, false, false, false), TerminalKey.Named(KeyName.F5) },
        { new ConsoleKeyInfo('\0', ConsoleKey.F12, true, false, false), TerminalKey.Named(KeyName.F12, shift: true) },
        { new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false), TerminalKey.Typed("a") },
        { new ConsoleKeyInfo('A', ConsoleKey.A, true, false, false), TerminalKey.Typed("A") },
        { new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false), TerminalKey.Typed(" ") },
        { new ConsoleKeyInfo('é', ConsoleKey.E, false, false, false), TerminalKey.Typed("é") },
        { new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true), TerminalKey.Control(0x03) },
        { new ConsoleKeyInfo('\0', ConsoleKey.C, false, false, true), TerminalKey.Control(0x03) },
        { new ConsoleKeyInfo('\0', ConsoleKey.A, false, false, true), TerminalKey.Control(0x01) },
        { new ConsoleKeyInfo('\0', ConsoleKey.Z, false, false, true), TerminalKey.Control(0x1A) },
        { new ConsoleKeyInfo('\u0004', ConsoleKey.D, false, false, true), TerminalKey.Control(0x04) },
        { new ConsoleKeyInfo('\0', ConsoleKey.Spacebar, false, false, true), TerminalKey.Control(0x00) },
        { new ConsoleKeyInfo('\u001C', ConsoleKey.Oem5, false, false, true), TerminalKey.Control(0x1C) },
        { new ConsoleKeyInfo('\0', ConsoleKey.NoName, false, false, false), null },
        { new ConsoleKeyInfo('\0', ConsoleKey.Oem4, false, false, true), null },
        { new ConsoleKeyInfo('\u0000', ConsoleKey.Packet, false, false, false), null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TranslatesEachKeyPress(ConsoleKeyInfo info, TerminalKey? expected)
    {
        var translator = new ConsoleKeyTranslator();

        Assert.Equal(expected, translator.Translate(info));
    }

    [Fact]
    public void JoinsASurrogatePairSplitAcrossTwoPresses()
    {
        var translator = new ConsoleKeyTranslator();

        Assert.Null(translator.Translate(new ConsoleKeyInfo('\uD83D', ConsoleKey.NoName, false, false, false)));
        Assert.Equal(
            TerminalKey.Typed("😀"),
            translator.Translate(new ConsoleKeyInfo('\uDE00', ConsoleKey.NoName, false, false, false)));
    }

    [Fact]
    public void ALoneLowSurrogateOrAbandonedHighSurrogateProducesNothing()
    {
        var translator = new ConsoleKeyTranslator();

        Assert.Null(translator.Translate(new ConsoleKeyInfo('\uDE00', ConsoleKey.NoName, false, false, false)));
        Assert.Null(translator.Translate(new ConsoleKeyInfo('\uD83D', ConsoleKey.NoName, false, false, false)));
        Assert.Equal(
            TerminalKey.Typed("x"),
            translator.Translate(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false)));
        Assert.Null(translator.Translate(new ConsoleKeyInfo('\uDE00', ConsoleKey.NoName, false, false, false)));
    }

    [Fact]
    public void ControlCFromTheConsoleIsAShellByteNotAnExit()
    {
        var translator = new ConsoleKeyTranslator();

        TerminalKey? key = translator.Translate(new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true));

        Assert.Equal(TerminalKeyKind.RawControl, key!.Value.Kind);
        Assert.Equal<byte>(0x03, key.Value.ControlByte);
    }
}
