#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using System.Text;
using Bazaar.Client.Calcinato.Modal;

namespace Bazaar.Client.Calcinato.Tests.Modal;

public sealed class TerminalKeyEncoderTests
{
    private const string Esc = "\u001b";

    public static IEnumerable<object[]> NamedKeys()
    {
        // name, shift, application cursor keys, expected sequence
        yield return new object[] { KeyName.Up, false, false, Esc + "[A" };
        yield return new object[] { KeyName.Up, false, true, Esc + "OA" };
        yield return new object[] { KeyName.Down, false, false, Esc + "[B" };
        yield return new object[] { KeyName.Down, false, true, Esc + "OB" };
        yield return new object[] { KeyName.Right, false, false, Esc + "[C" };
        yield return new object[] { KeyName.Right, false, true, Esc + "OC" };
        yield return new object[] { KeyName.Left, false, false, Esc + "[D" };
        yield return new object[] { KeyName.Left, false, true, Esc + "OD" };
        yield return new object[] { KeyName.Home, false, false, Esc + "[H" };
        yield return new object[] { KeyName.Home, false, true, Esc + "OH" };
        yield return new object[] { KeyName.End, false, false, Esc + "[F" };
        yield return new object[] { KeyName.End, false, true, Esc + "OF" };
        yield return new object[] { KeyName.Up, true, false, Esc + "[1;2A" };
        yield return new object[] { KeyName.Up, true, true, Esc + "[1;2A" };
        yield return new object[] { KeyName.Home, true, true, Esc + "[1;2H" };
        yield return new object[] { KeyName.Insert, false, true, Esc + "[2~" };
        yield return new object[] { KeyName.Delete, false, false, Esc + "[3~" };
        yield return new object[] { KeyName.PageUp, false, false, Esc + "[5~" };
        yield return new object[] { KeyName.PageDown, false, true, Esc + "[6~" };
        yield return new object[] { KeyName.PageDown, true, false, Esc + "[6;2~" };
        yield return new object[] { KeyName.F1, false, false, Esc + "OP" };
        yield return new object[] { KeyName.F2, false, true, Esc + "OQ" };
        yield return new object[] { KeyName.F3, false, false, Esc + "OR" };
        yield return new object[] { KeyName.F4, false, false, Esc + "OS" };
        yield return new object[] { KeyName.F1, true, false, Esc + "[1;2P" };
        yield return new object[] { KeyName.F5, false, false, Esc + "[15~" };
        yield return new object[] { KeyName.F6, false, false, Esc + "[17~" };
        yield return new object[] { KeyName.F7, false, false, Esc + "[18~" };
        yield return new object[] { KeyName.F8, false, false, Esc + "[19~" };
        yield return new object[] { KeyName.F9, false, false, Esc + "[20~" };
        yield return new object[] { KeyName.F10, false, false, Esc + "[21~" };
        yield return new object[] { KeyName.F11, false, false, Esc + "[23~" };
        yield return new object[] { KeyName.F12, false, false, Esc + "[24~" };
        yield return new object[] { KeyName.F12, true, false, Esc + "[24;2~" };
        yield return new object[] { KeyName.Enter, false, false, "\r" };
        yield return new object[] { KeyName.Tab, false, false, "\t" };
        yield return new object[] { KeyName.Tab, true, false, Esc + "[Z" };
        yield return new object[] { KeyName.Backspace, false, false, "\u007f" };
        yield return new object[] { KeyName.Escape, false, false, Esc };
    }

    [Theory]
    [MemberData(nameof(NamedKeys))]
    public void NamedKeysEncodeAsXtermSequences(KeyName name, bool shift, bool application, string expected)
    {
        var bytes = TerminalKeyEncoder.Encode(TerminalKey.Named(name, shift), application);

        Assert.Equal(Encoding.ASCII.GetBytes(expected), bytes);
    }

    [Theory]
    [InlineData("a", new byte[] { 0x61 })]
    [InlineData("é", new byte[] { 0xC3, 0xA9 })]
    [InlineData("€", new byte[] { 0xE2, 0x82, 0xAC })]
    [InlineData("ab", new byte[] { 0x61, 0x62 })]
    public void TextIsUtf8(string text, byte[] expected)
    {
        Assert.Equal(expected, TerminalKeyEncoder.Encode(TerminalKey.Typed(text), applicationCursorKeys: false));
    }

    [Theory]
    [InlineData((byte)0x01)]
    [InlineData((byte)0x03)]
    [InlineData((byte)0x04)]
    [InlineData((byte)0x1A)]
    public void RawControlBytesPassThrough(byte value)
    {
        Assert.Equal(new[] { value }, TerminalKeyEncoder.Encode(TerminalKey.Control(value), applicationCursorKeys: true));
    }

    [Fact]
    public void ControlBytesAboveTheControlRangeAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalKey.Control(0x20));
    }

    [Fact]
    public void NamedKeyNeedsAName()
    {
        Assert.Throws<ArgumentException>(() => TerminalKey.Named(KeyName.None));
    }
}
