using Bazaar.Server.Hive;

namespace Bazaar.Server.Tests.Hive;

public class HiveSanitizerTests
{
    private static readonly HiveSanitizerTerms _terms = new("project-atlas", ["Borealis"], ["Acme Corp"], ["Dana Smith"]);

    [Fact]
    public void Clean_text_passes()
    {
        Assert.True(HiveSanitizer.Check("Retry calls", "Use backoff with jitter.", _terms).IsClean);
    }

    [Theory]
    [InlineData("We did this in Project Atlas last week.", HiveSanitizerFinding.OriginProject)]
    [InlineData("see PROJECT_ATLAS notes", HiveSanitizerFinding.OriginProject)]
    [InlineData("the borealis build broke", HiveSanitizerFinding.Project)]
    [InlineData("Acme  Corp asked for it", HiveSanitizerFinding.Customer)]
    [InlineData("Dana Smith found it", HiveSanitizerFinding.Person)]
    [InlineData("key sk-ant-abcdefghijklmnopqrstuvwxyz0123", HiveSanitizerFinding.Secret)]
    public void Names_and_secrets_are_found(string body, HiveSanitizerFinding expected)
    {
        var result = HiveSanitizer.Check("Lesson", body, _terms);
        Assert.Contains(expected, result.Findings);
    }

    [Fact]
    public void Title_is_checked_too()
    {
        Assert.False(HiveSanitizer.Check("Fix for Acme Corp", "ok", _terms).IsClean);
    }

    [Fact]
    public void Whole_words_only()
    {
        var terms = new HiveSanitizerTerms("atlas", [], [], []);
        Assert.True(HiveSanitizer.Check("t", "The atlases and atlassian tools.", terms).IsClean);
        Assert.False(HiveSanitizer.Check("t", "Atlas, again.", terms).IsClean);
    }

    [Fact]
    public void Blank_terms_are_ignored_and_findings_are_distinct()
    {
        var terms = new HiveSanitizerTerms("x-origin", [" "], [""], []);
        Assert.True(HiveSanitizer.Check("t", "plain", terms).IsClean);
        var result = HiveSanitizer.Check("t", "x-origin and X Origin", terms);
        Assert.Single(result.Findings);
    }
}
