using Bazaar.Client.Core.Accessibility;

namespace Bazaar.Client.Core.Tests.Accessibility;

public class AccessibleNameTests
{
    [Theory]
    [InlineData("Send message")]
    [InlineData("  Unread mentions  ")]
    [InlineData("Constellation 2D")]
    public void Usable_names_pass(string name)
    {
        Assert.True(AccessibleName.IsValid(name));
        Assert.Null(AccessibleName.Problem(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_names_fail(string? name)
    {
        Assert.Equal("The control has no accessible name.", AccessibleName.Problem(name));
    }

    [Theory]
    [InlineData("...")]
    [InlineData("--")]
    public void Names_without_letters_or_digits_fail(string name)
    {
        Assert.NotNull(AccessibleName.Problem(name));
    }

    [Theory]
    [InlineData("button")]
    [InlineData("TODO")]
    [InlineData("icon")]
    public void Placeholder_names_fail(string name)
    {
        Assert.Contains("placeholder", AccessibleName.Problem(name));
    }

    [Fact]
    public void Names_longer_than_the_limit_fail()
    {
        string tooLong = new('a', AccessibleName.MaxLength + 1);

        Assert.NotNull(AccessibleName.Problem(tooLong));
        Assert.True(AccessibleName.IsValid(new string('a', AccessibleName.MaxLength)));
    }
}
