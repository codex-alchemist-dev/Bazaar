using Avalonia.Input;
using Bazaar.Client.Calcinato.Modal;

namespace Bazaar.Client.Calcinato.Avalonia.Tests;

public class TerminalKeyMapperTests
{
    [Theory]
    [InlineData(Key.Left, KeyName.Left, false)]
    [InlineData(Key.Right, KeyName.Right, false)]
    [InlineData(Key.Up, KeyName.Up, false)]
    [InlineData(Key.Down, KeyName.Down, false)]
    [InlineData(Key.Home, KeyName.Home, false)]
    [InlineData(Key.End, KeyName.End, false)]
    [InlineData(Key.PageUp, KeyName.PageUp, false)]
    [InlineData(Key.PageDown, KeyName.PageDown, false)]
    [InlineData(Key.Insert, KeyName.Insert, false)]
    [InlineData(Key.Delete, KeyName.Delete, false)]
    [InlineData(Key.Back, KeyName.Backspace, false)]
    [InlineData(Key.Enter, KeyName.Enter, false)]
    [InlineData(Key.Escape, KeyName.Escape, false)]
    [InlineData(Key.Tab, KeyName.Tab, false)]
    [InlineData(Key.F1, KeyName.F1, false)]
    [InlineData(Key.F5, KeyName.F5, false)]
    [InlineData(Key.F12, KeyName.F12, false)]
    public void Named_keys_map_to_named_keys(Key key, KeyName name, bool shift)
    {
        Assert.True(TerminalKeyMapper.TryMapKey(key, KeyModifiers.None, out TerminalKey mapped));
        Assert.Equal(TerminalKey.Named(name, shift), mapped);
    }

    [Fact]
    public void Shift_tab_is_named_tab_with_shift()
    {
        Assert.True(TerminalKeyMapper.TryMapKey(Key.Tab, KeyModifiers.Shift, out TerminalKey mapped));
        Assert.Equal(TerminalKey.Named(KeyName.Tab, shift: true), mapped);
    }

    [Fact]
    public void Shift_arrow_keeps_the_shift_flag()
    {
        Assert.True(TerminalKeyMapper.TryMapKey(Key.PageUp, KeyModifiers.Shift, out TerminalKey mapped));
        Assert.Equal(TerminalKey.Named(KeyName.PageUp, shift: true), mapped);
    }

    [Fact]
    public void Ctrl_c_is_byte_three_so_the_shell_receives_it()
    {
        Assert.True(TerminalKeyMapper.TryMapKey(Key.C, KeyModifiers.Control, out TerminalKey mapped));
        Assert.Equal(TerminalKey.Control(3), mapped);
        Assert.Equal(TerminalKeyKind.RawControl, mapped.Kind);
        Assert.Equal((byte)3, mapped.ControlByte);
    }

    [Theory]
    [InlineData(Key.A, (byte)1)]
    [InlineData(Key.D, (byte)4)]
    [InlineData(Key.Z, (byte)26)]
    public void Ctrl_letters_map_to_their_control_bytes(Key key, byte value)
    {
        Assert.True(TerminalKeyMapper.TryMapKey(key, KeyModifiers.Control, out TerminalKey mapped));
        Assert.Equal(TerminalKey.Control(value), mapped);
    }

    [Fact]
    public void Ctrl_space_is_the_nul_byte()
    {
        Assert.True(TerminalKeyMapper.TryMapKey(Key.Space, KeyModifiers.Control, out TerminalKey mapped));
        Assert.Equal(TerminalKey.Control(0), mapped);
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.D1)]
    [InlineData(Key.Space)]
    public void Plain_text_keys_are_left_to_text_input(Key key)
    {
        Assert.False(TerminalKeyMapper.TryMapKey(key, KeyModifiers.None, out _));
        Assert.False(TerminalKeyMapper.TryMapKey(key, KeyModifiers.Shift, out _));
    }

    [Fact]
    public void Committed_text_maps_to_a_text_key()
    {
        Assert.True(TerminalKeyMapper.FromText("x", out TerminalKey mapped));
        Assert.Equal(TerminalKey.Typed("x"), mapped);
    }

    [Fact]
    public void Ime_committed_text_is_passed_through_whole()
    {
        Assert.True(TerminalKeyMapper.FromText("中文", out TerminalKey mapped));
        Assert.Equal(TerminalKeyKind.Text, mapped.Kind);
        Assert.Equal("中文", mapped.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r")]
    [InlineData("\t")]
    [InlineData("\u007f")]
    public void Empty_and_control_text_is_not_text(string? text)
    {
        Assert.False(TerminalKeyMapper.FromText(text, out _));
    }
}
