using Bazaar.Client.Core.Commands;

namespace Bazaar.Client.Core.Tests.Commands;

public class CommandTests
{
    [Theory]
    [InlineData("w c")]
    [InlineData("gc")]
    [InlineData("v2")]
    [InlineData("A")]
    public void Accepts_letters_digits_and_inner_spaces(string mnemonic)
    {
        var command = new Command("id", "Title", mnemonic, null, CommandSource.Local);

        Assert.Equal(mnemonic, command.Mnemonic);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" gc")]
    [InlineData("gc ")]
    [InlineData("ctrl+s")]
    [InlineData("g-c")]
    [InlineData("<esc>")]
    public void Rejects_empty_or_non_alphanumeric_mnemonics(string mnemonic)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new Command("id", "Title", mnemonic, null, CommandSource.Local));
    }

    [Fact]
    public void Enabled_is_evaluated_each_time_it_is_asked()
    {
        var enabled = true;
        var command = new Command("id", "Title", "t", null, CommandSource.Local, () => enabled);

        Assert.True(command.IsEnabled());
        enabled = false;
        Assert.False(command.IsEnabled());
    }

    [Fact]
    public void Missing_enabled_predicate_means_enabled()
    {
        var command = new Command("id", "Title", "t", null, CommandSource.Local);

        Assert.True(command.IsEnabled());
    }

    [Fact]
    public void Null_keywords_become_an_empty_list()
    {
        var command = new Command("id", "Title", "t", null, CommandSource.Local);

        Assert.Empty(command.Keywords);
    }
}
