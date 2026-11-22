using Bazaar.Client.GitGui.Github;

namespace Bazaar.Client.GitGui.Github.Tests;

public class GitHubProviderUiTests
{
    private static readonly GitHubProviderUi _ui = new();

    [Fact]
    public void Identity_is_github()
    {
        Assert.Equal("github", _ui.ProviderId);
        Assert.Equal("GitHub", _ui.DisplayName);
        Assert.Equal("pull request", _ui.PrNoun);
    }

    [Theory]
    [InlineData("pending", "Pending")]
    [InlineData("success", "Passed")]
    [InlineData("failure", "Failed")]
    [InlineData("skipped", "Skipped")]
    public void Check_states_map_to_labels(string state, string expected) =>
        Assert.Equal(expected, _ui.CheckLabel(state));

    [Theory]
    [InlineData("queued")]
    [InlineData("Success")]
    [InlineData("")]
    [InlineData("in_progress")]
    public void Unknown_check_states_pass_through(string state) =>
        Assert.Equal(state, _ui.CheckLabel(state));

    [Theory]
    [InlineData("comment", "Commented")]
    [InlineData("requestchanges", "Changes requested")]
    [InlineData("approve", "Approved")]
    public void Review_verdicts_map_to_labels(string verdict, string expected) =>
        Assert.Equal(expected, _ui.ReviewLabel(verdict));

    [Theory]
    [InlineData("dismissed")]
    [InlineData("APPROVE")]
    [InlineData("")]
    public void Unknown_review_verdicts_pass_through(string verdict) =>
        Assert.Equal(verdict, _ui.ReviewLabel(verdict));

    [Fact]
    public void Pr_url_has_the_pull_shape()
    {
        Assert.Equal(new Uri("https://github.com/octo-org/hello.world/pull/42"), _ui.PrWebUrl("octo-org/hello.world", 42));
        Assert.Equal(new Uri("https://github.com/a/b/pull/1"), _ui.PrWebUrl("a/b", 1));
    }

    [Fact]
    public void Commit_url_has_the_commit_shape_and_lowercase_sha()
    {
        Assert.Equal(
            new Uri("https://github.com/octo/hello/commit/0123456789abcdef0123456789abcdef01234567"),
            _ui.CommitWebUrl("octo/hello", "0123456789ABCDEF0123456789abcdef01234567"));
        Assert.Equal(new Uri("https://github.com/octo/hello/commit/abcdef1"), _ui.CommitWebUrl("octo/hello", "abcdef1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("octo")]
    [InlineData("octo/hello/extra")]
    [InlineData("/hello")]
    [InlineData("octo/")]
    [InlineData("octo//hello")]
    [InlineData("../hello")]
    [InlineData("octo/..")]
    [InlineData("octo/.")]
    [InlineData("octo/..hello")]
    [InlineData("octo/%2e%2e")]
    [InlineData("octo/%2e")]
    [InlineData("octo/hello?x=1")]
    [InlineData("octo/hello#frag")]
    [InlineData("octo/hello world")]
    [InlineData("octo/hello@evil.example")]
    [InlineData("octo/hel\\lo")]
    [InlineData("octo/héllo")]
    [InlineData(" octo/hello")]
    [InlineData("octo/hello\n")]
    public void Invalid_repo_names_return_null(string repo)
    {
        Assert.Null(_ui.PrWebUrl(repo, 1));
        Assert.Null(_ui.CommitWebUrl(repo, "abcdef1"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Non_positive_pr_numbers_return_null(long number) =>
        Assert.Null(_ui.PrWebUrl("octo/hello", number));

    [Theory]
    [InlineData("abc123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("xyz1234")]
    [InlineData("abc1234 ")]
    [InlineData("abc/1234")]
    [InlineData("../../abc1234")]
    [InlineData("abc1234?x")]
    [InlineData("")]
    public void Invalid_shas_return_null(string sha) =>
        Assert.Null(_ui.CommitWebUrl("octo/hello", sha));

    [Fact]
    public void Sha_length_bounds_are_seven_to_sixty_four()
    {
        Assert.NotNull(_ui.CommitWebUrl("octo/hello", new string('a', 7)));
        Assert.NotNull(_ui.CommitWebUrl("octo/hello", new string('a', 64)));
    }

    [Fact]
    public void Module_exposes_the_github_ui_once()
    {
        Assert.Same(GitHubProviderUiModule.Instance, GitHubProviderUiModule.ProviderUis.Single());
        Assert.Equal("github", GitHubProviderUiModule.Instance.ProviderId);
    }
}
