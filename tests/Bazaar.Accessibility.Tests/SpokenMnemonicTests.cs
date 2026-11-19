using Bazaar.Accessibility;

namespace Bazaar.Accessibility.Tests;

public class SpokenMnemonicTests
{
    [Theory]
    [InlineData("ngc1", "N G C 1")]
    [InlineData("TOPGC", "T O P G C")]
    [InlineData("unread", "U N R E A D")]
    public void Spells_each_character(string mnemonic, string expected)
    {
        Assert.Equal(expected, SpokenMnemonic.Speak(mnemonic));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Blank_mnemonic_is_rejected(string mnemonic)
    {
        Assert.Throws<ArgumentException>(() => SpokenMnemonic.Speak(mnemonic));
    }

    [Theory]
    [InlineData("ng-c")]
    [InlineData("ctrl+s")]
    [InlineData("é")]
    public void Characters_that_cannot_be_typed_are_rejected(string mnemonic)
    {
        Assert.Throws<ArgumentException>(() => SpokenMnemonic.Speak(mnemonic));
    }

    [Fact]
    public void Announcement_names_the_command_then_its_mnemonic()
    {
        Assert.Equal("Constellation 2D, mnemonic N G C 2", CommandAnnouncement.Format("Constellation 2D", "ngc2"));
    }

    [Fact]
    public void Announcement_trims_the_label()
    {
        Assert.Equal("Unread, mnemonic U N R E A D", CommandAnnouncement.Format("  Unread  ", "unread"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("button")]
    public void Announcement_rejects_an_unusable_label(string label)
    {
        Assert.Throws<ArgumentException>(() => CommandAnnouncement.Format(label, "ngc1"));
    }
}
