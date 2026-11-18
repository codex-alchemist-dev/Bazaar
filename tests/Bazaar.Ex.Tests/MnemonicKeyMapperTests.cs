using Avalonia.Input;
using Bazaar.Client.Core.Input;
using Bazaar.Ex.Head;
using AvaloniaModifiers = Avalonia.Input.KeyModifiers;
using CoreModifiers = Bazaar.Client.Core.Input.KeyModifiers;

namespace Bazaar.Ex.Tests;

public class MnemonicKeyMapperTests
{
    [Fact]
    public void Letters_map_to_lower_case_without_shift()
    {
        Assert.True(MnemonicKeyMapper.TryMap(Key.N, null, AvaloniaModifiers.None, out var stroke));
        Assert.Equal('n', stroke.Character);
    }

    [Fact]
    public void Shift_on_a_letter_maps_to_upper_case_and_keeps_the_shift_flag()
    {
        Assert.True(MnemonicKeyMapper.TryMap(Key.W, null, AvaloniaModifiers.Shift, out var stroke));
        Assert.Equal('W', stroke.Character);
        Assert.True((stroke.Modifiers & CoreModifiers.Shift) != 0);
    }

    [Fact]
    public void A_printable_symbol_wins_over_the_key_when_present()
    {
        Assert.True(MnemonicKeyMapper.TryMap(Key.D1, "!", AvaloniaModifiers.Shift, out var stroke));
        Assert.Equal('!', stroke.Character);
    }

    [Fact]
    public void Digits_and_space_map_to_their_characters()
    {
        Assert.True(MnemonicKeyMapper.TryMap(Key.D1, null, AvaloniaModifiers.None, out var one));
        Assert.Equal('1', one.Character);
        Assert.True(MnemonicKeyMapper.TryMap(Key.Space, null, AvaloniaModifiers.None, out var space));
        Assert.Equal(' ', space.Character);
    }

    [Fact]
    public void Named_keys_map_to_named_keys()
    {
        Assert.True(MnemonicKeyMapper.TryMap(Key.Enter, null, AvaloniaModifiers.None, out var enter));
        Assert.Equal(NamedKey.Enter, enter.Named);
        Assert.True(MnemonicKeyMapper.TryMap(Key.Back, null, AvaloniaModifiers.None, out var back));
        Assert.Equal(NamedKey.Backspace, back.Named);
    }

    [Fact]
    public void Control_is_carried_so_the_router_can_refuse_the_chord()
    {
        Assert.True(MnemonicKeyMapper.TryMap(Key.W, null, AvaloniaModifiers.Control, out var stroke));
        Assert.True(stroke.HasChordModifier);
    }

    [Fact]
    public void Keys_it_does_not_handle_are_left_alone()
    {
        Assert.False(MnemonicKeyMapper.TryMap(Key.F1, null, AvaloniaModifiers.None, out _));
    }
}
