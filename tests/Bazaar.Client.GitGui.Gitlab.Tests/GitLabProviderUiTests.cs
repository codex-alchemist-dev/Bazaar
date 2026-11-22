using Bazaar.Client.GitGui.Gitlab;

namespace Bazaar.Client.GitGui.Gitlab.Tests;

public class GitLabProviderUiTests
{
    private static readonly GitLabProviderUi _ui = new();

    [Fact]
    public void Identity_is_gitlab()
    {
        Assert.Equal("gitlab", _ui.ProviderId);
        Assert.Equal("GitLab", _ui.DisplayName);
        Assert.Equal("merge request", _ui.PrNoun);
    }

    [Theory]
    [InlineData("pending", "Running")]
    [InlineData("success", "Passed")]
    [InlineData("failure", "Failed")]
    [InlineData("skipped", "Skipped")]
    public void Check_states_map_to_labels(string state, string expected) =>
        Assert.Equal(expected, _ui.CheckLabel(state));

    [Theory]
    [InlineData("queued")]
    [InlineData("Pending")]
    [InlineData("")]
    [InlineData("manual")]
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
    [InlineData("Approve")]
    [InlineData("")]
    public void Unknown_review_verdicts_pass_through(string verdict) =>
        Assert.Equal(verdict, _ui.ReviewLabel(verdict));

    [Fact]
    public void Merge_request_url_has_the_dash_shape_on_gitlab_com()
    {
        Assert.Equal(new Uri("https://gitlab.com/group/project/-/merge_requests/7"), _ui.PrWebUrl("group/project", 7));
    }

    [Fact]
    public void Subgroup_paths_are_kept_whole()
    {
        Assert.Equal(
            new Uri("https://gitlab.com/org/team/sub/project/-/merge_requests/7"),
            _ui.PrWebUrl("org/team/sub/project", 7));
        Assert.Equal(
            new Uri("https://gitlab.com/org/team/sub/project/-/commit/abcdef12"),
            _ui.CommitWebUrl("org/team/sub/project", "abcdef12"));
    }

    [Fact]
    public void Commit_url_has_the_dash_commit_shape_and_lowercase_sha()
    {
        Assert.Equal(
            new Uri("https://gitlab.com/group/project/-/commit/0123456789abcdef0123456789abcdef01234567"),
            _ui.CommitWebUrl("group/project", "0123456789ABCDEF0123456789abcdef01234567"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("single")]
    [InlineData("a//b")]
    [InlineData("/a/b")]
    [InlineData("a/b/")]
    [InlineData("../b/c")]
    [InlineData("a/../b")]
    [InlineData("a/..")]
    [InlineData("a/.")]
    [InlineData("a/%2e/b")]
    [InlineData("a/%2e%2e/b")]
    [InlineData("a/b?x=1")]
    [InlineData("a/b#frag")]
    [InlineData("a b/c")]
    [InlineData("a/b@evil.example")]
    [InlineData("a/-/b")]
    [InlineData("a/b/-")]
    [InlineData("a/b\\c")]
    [InlineData("a/bé")]
    public void Invalid_project_paths_return_null(string path)
    {
        Assert.Null(_ui.PrWebUrl(path, 1));
        Assert.Null(_ui.CommitWebUrl(path, "abcdef1"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(long.MinValue)]
    public void Non_positive_mr_numbers_return_null(long number) =>
        Assert.Null(_ui.PrWebUrl("group/project", number));

    [Theory]
    [InlineData("abc123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("xyz1234")]
    [InlineData("abc1234 ")]
    [InlineData("abc/1234")]
    [InlineData("../../abc1234")]
    [InlineData("")]
    public void Invalid_shas_return_null(string sha) =>
        Assert.Null(_ui.CommitWebUrl("group/project", sha));

    [Fact]
    public void Default_options_target_gitlab_com()
    {
        Assert.Equal("https://gitlab.com", new GitLabProviderUiOptions().BaseUrl);
    }

    [Theory]
    [InlineData("https://gitlab.example.com")]
    [InlineData("https://gitlab.example.com/")]
    [InlineData("https://gitlab.example.com//")]
    public void Self_managed_base_url_is_normalised_without_trailing_slash(string baseUrl)
    {
        var ui = new GitLabProviderUi(new GitLabProviderUiOptions { BaseUrl = baseUrl });

        Assert.Equal(
            new Uri("https://gitlab.example.com/grp/proj/-/merge_requests/3"),
            ui.PrWebUrl("grp/proj", 3));
        Assert.Equal(
            new Uri("https://gitlab.example.com/grp/proj/-/commit/abcdef1"),
            ui.CommitWebUrl("grp/proj", "abcdef1"));
    }

    [Fact]
    public void Self_managed_base_url_may_carry_a_path_prefix()
    {
        var ui = new GitLabProviderUi(new GitLabProviderUiOptions { BaseUrl = "https://example.com/gitlab/" });

        Assert.Equal(new Uri("https://example.com/gitlab/grp/proj/-/merge_requests/3"), ui.PrWebUrl("grp/proj", 3));
    }

    [Theory]
    [InlineData("http://gitlab.example.com")]
    [InlineData("ftp://gitlab.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gitlab.example.com")]
    [InlineData("https://")]
    [InlineData("https:///path")]
    [InlineData("https://user:secret@gitlab.example.com")]
    [InlineData("https://user@gitlab.example.com")]
    [InlineData("https://gitlab.example.com?x=1")]
    [InlineData("https://gitlab.example.com/?x=1")]
    [InlineData("https://gitlab.example.com#frag")]
    [InlineData("https://gitlab.example.com/a\\b")]
    [InlineData("https://gitlab.example.com/a b")]
    [InlineData("https://gitlab.example.com\n")]
    [InlineData("   ")]
    [InlineData("")]
    public void Unacceptable_base_urls_are_refused(string baseUrl)
    {
        Assert.ThrowsAny<ArgumentException>(() => new GitLabProviderUi(new GitLabProviderUiOptions { BaseUrl = baseUrl }));
    }

    [Fact]
    public void Module_exposes_the_gitlab_com_ui_once()
    {
        Assert.Same(GitLabProviderUiModule.Instance, GitLabProviderUiModule.ProviderUis.Single());
        Assert.Equal("gitlab", GitLabProviderUiModule.Instance.ProviderId);
    }

    [Fact]
    public void Module_builds_a_self_managed_ui_from_options()
    {
        var uis = GitLabProviderUiModule.ProviderUisFor(new GitLabProviderUiOptions { BaseUrl = "https://git.example.org" });

        var ui = Assert.Single(uis);
        Assert.Equal("gitlab", ui.ProviderId);
        Assert.Equal(new Uri("https://git.example.org/a/b/-/merge_requests/9"), ui.PrWebUrl("a/b", 9));
    }
}
