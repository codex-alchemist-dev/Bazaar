using Bazaar.Diagnostics.Grouping;

namespace Bazaar.Diagnostics.Tests.Grouping;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("0.1.0-alpha", "0.1.0")]
    [InlineData("0.1.0-alpha", "0.1.0-alpha.1")]
    [InlineData("0.1.0-alpha.2", "0.1.0-alpha.10")]
    [InlineData("0.1.0-alpha.1", "0.1.0-beta")]
    public void Lower_sorts_before_higher(string lower, string higher)
    {
        Assert.True(ReleaseVersion.Parse(lower) < ReleaseVersion.Parse(higher));
        Assert.True(ReleaseVersion.Parse(higher) > ReleaseVersion.Parse(lower));
    }

    [Fact]
    public void Build_metadata_does_not_change_precedence_and_the_text_is_kept()
    {
        var withCommit = ReleaseVersion.Parse("0.2.0+a1b2c3d");
        var plain = ReleaseVersion.Parse("0.2.0");

        Assert.True(withCommit == plain);
        Assert.Equal("0.2.0+a1b2c3d", withCommit.Text);
    }

    [Fact]
    public void Leading_v_from_a_release_tag_is_accepted()
    {
        Assert.True(ReleaseVersion.Parse("v0.1.0") == ReleaseVersion.Parse("0.1.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("a.b.c")]
    [InlineData("01.2.3")]
    public void Garbage_does_not_parse(string text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }
}
