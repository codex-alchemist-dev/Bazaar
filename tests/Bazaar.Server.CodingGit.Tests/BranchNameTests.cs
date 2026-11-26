namespace Bazaar.Server.CodingGit.Tests;

public sealed class BranchNameTests
{
    [Theory]
    [InlineData("agent/fix-login")]
    [InlineData("agent/a")]
    [InlineData("agent/task_1.2-x")]
    [InlineData("agent/v1.0")]
    public void Valid_names_parse(string value)
    {
        Assert.True(BranchName.TryParse(value, out var name));
        Assert.Equal(value, name.Value);
        Assert.Equal(value["agent/".Length..], name.TaskSlug);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("agent/")]
    [InlineData("main")]
    [InlineData("agent")]
    [InlineData("Agent/x")]
    [InlineData("agents/x")]
    [InlineData("refs/heads/agent/x")]
    [InlineData("feature/x")]
    [InlineData("agent/a..b")]
    [InlineData("agent/..")]
    [InlineData("agent/.hidden")]
    [InlineData("agent/-flag")]
    [InlineData("agent/x.")]
    [InlineData("agent/x.lock")]
    [InlineData("agent/UPPER")]
    [InlineData("agent/has space")]
    [InlineData("agent/a/b")]
    [InlineData("agent/a\\b")]
    [InlineData("agent/a:b")]
    [InlineData("agent/a~1")]
    [InlineData("agent/a@{1}")]
    [InlineData("agent/x\n")]
    [InlineData(" agent/x")]
    public void Invalid_names_are_rejected(string? value)
    {
        Assert.False(BranchName.TryParse(value, out var name));
        Assert.True(name.IsDefault);
        var ex = Assert.Throws<CodingGitException>(() => BranchName.Parse(value));
        Assert.Equal(CodingGitError.InvalidBranch, ex.Error);
    }

    [Fact]
    public void Slug_length_is_capped()
    {
        Assert.True(BranchName.TryParse("agent/" + new string('a', BranchName.MaxTaskLength), out _));
        Assert.False(BranchName.TryParse("agent/" + new string('a', BranchName.MaxTaskLength + 1), out _));
    }

    [Fact]
    public void Default_value_carries_no_name()
    {
        var none = default(BranchName);
        Assert.True(none.IsDefault);
        Assert.Throws<InvalidOperationException>(() => none.Value);
        Assert.Equal(BranchName.ForTask("x"), BranchName.Parse("agent/x"));
        Assert.NotEqual(none, BranchName.ForTask("x"));
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("Main", "main")]
    [InlineData("refs/heads/main", "main")]
    [InlineData(" main ", "main")]
    [InlineData("anything", "")]
    [InlineData("", "main")]
    public void Guard_flags_the_default_branch_and_fails_closed_on_blanks(string branch, string defaultBranch)
    {
        Assert.True(DefaultBranchGuard.IsDefaultBranch(branch, defaultBranch));
        var ex = Assert.Throws<CodingGitException>(() => DefaultBranchGuard.RequireAgentBranch(branch, defaultBranch));
        Assert.Equal(CodingGitError.DefaultBranchRefused, ex.Error);
    }

    [Theory]
    [InlineData("feature/x")]
    [InlineData("develop")]
    [InlineData("agent/x.lock")]
    public void Guard_refuses_non_agent_branches(string branch)
    {
        var ex = Assert.Throws<CodingGitException>(() => DefaultBranchGuard.RequireAgentBranch(branch, "main"));
        Assert.Equal(CodingGitError.BranchRefused, ex.Error);
    }

    [Fact]
    public void Guard_accepts_an_agent_branch()
    {
        Assert.Equal("agent/x", DefaultBranchGuard.RequireAgentBranch("agent/x", "main").Value);
    }
}
