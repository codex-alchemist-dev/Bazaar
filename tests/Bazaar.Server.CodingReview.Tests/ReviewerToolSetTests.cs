namespace Bazaar.Server.CodingReview.Tests;

public sealed class ReviewerToolSetTests
{
    [Theory]
    [InlineData("shell")]
    [InlineData("bash")]
    [InlineData("exec")]
    [InlineData("run_command")]
    [InlineData("edit")]
    [InlineData("write_file")]
    [InlineData("push")]
    [InlineData("git_push")]
    [InlineData("git_commit")]
    [InlineData("open_draft_pr")]
    [InlineData("run_checks")]
    [InlineData("web_fetch")]
    [InlineData("http_get")]
    [InlineData("READ")]
    [InlineData(" read")]
    public void Tools_outside_the_allowlist_are_refused(string toolName)
    {
        var decision = ReviewerToolSet.Authorize(toolName);

        Assert.False(decision.Allowed);
        Assert.Contains(toolName, decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_tool_names_are_refused(string? toolName)
    {
        Assert.False(ReviewerToolSet.Authorize(toolName).Allowed);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("list")]
    [InlineData("search")]
    [InlineData("git_diff")]
    [InlineData("git_show")]
    [InlineData("git_log")]
    public void Read_only_tools_are_allowed(string toolName)
    {
        Assert.True(ReviewerToolSet.Authorize(toolName).Allowed);
    }

    [Fact]
    public void The_allowlist_is_exactly_the_six_read_only_tools()
    {
        Assert.Equal(["read", "list", "search", "git_diff", "git_show", "git_log"], ReviewerToolSet.AllowedToolNames);
    }
}
