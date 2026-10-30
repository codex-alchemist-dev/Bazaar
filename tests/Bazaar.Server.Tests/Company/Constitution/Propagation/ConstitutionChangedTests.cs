using Bazaar.Server.Company.Constitution.Propagation;

namespace Bazaar.Server.Tests.Company.Constitution.Propagation;

public class ConstitutionChangedTests
{
    private const string _from = "1111111111111111111111111111111111111111";
    private const string _to = "2222222222222222222222222222222222222222";

    [Fact]
    public void Accepts_valid_shas_and_keeps_section_order()
    {
        var change = new ConstitutionChanged(_from, _to, ["Values", "Escalation"]);

        Assert.Equal(_from, change.From);
        Assert.Equal(_to, change.To);
        Assert.Equal(["Values", "Escalation"], change.Sections);
    }

    [Fact]
    public void Accepts_a_null_from_for_the_first_activation()
    {
        var change = new ConstitutionChanged(null, _to, ["Values"]);

        Assert.Null(change.From);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("111111111111111111111111111111111111111")]
    [InlineData("11111111111111111111111111111111111111111")]
    [InlineData("ABCDEF1111111111111111111111111111111111")]
    [InlineData("zz11111111111111111111111111111111111111")]
    public void Refuses_a_malformed_sha(string sha)
    {
        var ex = Assert.Throws<ArgumentException>(() => new ConstitutionChanged(_from, sha, ["Values"]));

        Assert.Equal("to", ex.ParamName);
    }

    [Fact]
    public void Refuses_a_malformed_from_sha()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ConstitutionChanged("not-a-sha", _to, ["Values"]));

        Assert.Equal("from", ex.ParamName);
    }

    [Fact]
    public void Refuses_from_equal_to_to()
    {
        Assert.Throws<ArgumentException>(() => new ConstitutionChanged(_to, _to, ["Values"]));
    }

    [Fact]
    public void Refuses_an_empty_or_blank_or_duplicate_section_list()
    {
        Assert.Throws<ArgumentException>(() => new ConstitutionChanged(_from, _to, []));
        Assert.Throws<ArgumentException>(() => new ConstitutionChanged(_from, _to, ["  "]));
        Assert.Throws<ArgumentException>(() => new ConstitutionChanged(_from, _to, ["Values", "Values"]));
    }
}
