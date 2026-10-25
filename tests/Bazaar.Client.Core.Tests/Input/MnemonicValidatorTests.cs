using Bazaar.Client.Core.Input;

namespace Bazaar.Client.Core.Tests.Input;

public class MnemonicValidatorTests
{
    [Theory]
    [InlineData("w")]
    [InlineData("wc")]
    [InlineData("G7")]
    [InlineData("a b")]
    [InlineData("  ")]
    public void Letters_digits_and_space_are_valid(string text)
    {
        Assert.Null(Mnemonic.Validate(text));
    }

    [Fact]
    public void Shift_is_allowed_because_the_character_already_carries_case()
    {
        var keys = new[] { KeyStroke.OfChar('W', KeyModifiers.Shift) };

        Assert.Null(Mnemonic.Validate(keys));
    }

    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Meta)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift)]
    public void Ctrl_Alt_and_Meta_are_rejected(KeyModifiers modifiers)
    {
        var keys = new[] { KeyStroke.OfChar('w', modifiers) };

        Assert.NotNull(Mnemonic.Validate(keys));
    }

    [Fact]
    public void Named_keys_are_rejected()
    {
        Assert.NotNull(Mnemonic.Validate([KeyStroke.OfKey(NamedKey.Enter)]));
        Assert.NotNull(Mnemonic.Validate([KeyStroke.OfKey(NamedKey.Escape)]));
    }

    [Theory]
    [InlineData("-")]
    [InlineData("+")]
    [InlineData("\t")]
    public void Punctuation_and_control_characters_are_rejected(string text)
    {
        Assert.NotNull(Mnemonic.Validate(text));
    }

    [Fact]
    public void An_empty_mnemonic_is_rejected()
    {
        Assert.NotNull(Mnemonic.Validate(string.Empty));
    }

    [Fact]
    public void ToText_returns_the_characters_in_order()
    {
        Assert.Equal("wc ", Mnemonic.ToText(InputTestData.Keys("wc ")));
    }
}
