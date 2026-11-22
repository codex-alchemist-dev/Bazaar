using Microsoft.Extensions.DependencyInjection;
using static Bazaar.Server.Community.GitHub.Tests.TestSupport;

namespace Bazaar.Server.Community.GitHub.Tests;

public sealed class GitHubCommunityAdapterTests
{
    private readonly FakeCommunityGitHubApi _api = new();

    private readonly RecordingPullRequestOpener _prs = new();

    private GitHubCommunityAdapter Adapter() => new(_api, _prs, new DefaultCommunityFooterCheck());

    private static async Task<CommunityPostException> Refused(Task task)
    {
        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => task);
        Assert.False(ex.Retryable);
        return ex;
    }

    [Fact]
    public void Platform_is_github() => Assert.Equal("github", Adapter().Platform);

    [Fact]
    public async Task Hash_mismatch_is_refused_and_nothing_is_posted()
    {
        var post = Post() with { Body = "Edited after approval.\n\n" + Footer };

        await Refused(Adapter().PostAsync(post, Approval, CancellationToken.None));

        Assert.Empty(_api.Items);
    }

    [Fact]
    public async Task Missing_footer_is_refused()
    {
        var post = Post(body: "No disclosure here.");

        var ex = await Refused(Adapter().PostAsync(post, Approval, CancellationToken.None));

        Assert.Contains("footer", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_api.Items);
    }

    [Fact]
    public async Task Footer_check_is_the_injected_seam()
    {
        var adapter = new GitHubCommunityAdapter(_api, _prs, new RejectingFooterCheck());

        await Refused(adapter.PostAsync(Post(), Approval, CancellationToken.None));
    }

    [Theory]
    [InlineData("Fix.\n\nSigned-off-by: Bot <bot@example.org>\n\n" + Footer)]
    [InlineData("Fix.\n\n  signed-off-by: Bot <bot@example.org>\n" + Footer)]
    public async Task Signed_off_by_line_is_refused(string body)
    {
        var post = Post("pr", body);

        var ex = await Refused(Adapter().PostAsync(post, Approval, CancellationToken.None));

        Assert.Contains("Signed-off-by", ex.Message);
        Assert.Empty(_api.Items);
    }

    [Fact]
    public async Task Assisted_by_trailer_from_the_caller_passes_through_untouched()
    {
        var body = "Fix.\n\nAssisted-by: Claude:claude-sonnet-5-5\n\n" + Footer;

        await Adapter().PostAsync(Post("pr", body), Approval, CancellationToken.None);

        var sent = Assert.Single(_prs.Requests).Body;
        Assert.Contains("Assisted-by: Claude:claude-sonnet-5-5", sent);
        Assert.DoesNotContain("Signed-off-by", sent);
    }

    [Fact]
    public async Task Default_approval_id_is_refused()
    {
        await Refused(Adapter().PostAsync(Post(), default, CancellationToken.None));

        Assert.Empty(_api.Items);
    }

    [Theory]
    [InlineData("thread")]
    [InlineData("discussion")]
    public async Task Unsupported_kind_is_refused(string kind)
    {
        await Refused(Adapter().PostAsync(Post() with { Kind = kind }, Approval, CancellationToken.None));
    }

    [Fact]
    public async Task Other_platform_is_refused()
    {
        await Refused(Adapter().PostAsync(Post(platform: "reddit"), Approval, CancellationToken.None));
    }

    [Theory]
    [InlineData("issue", "acme")]
    [InlineData("issue", "acme/widgets#3")]
    [InlineData("comment", "acme/widgets")]
    [InlineData("comment", "acme/widgets#abc")]
    [InlineData("issue", "../etc/passwd")]
    public async Task Bad_target_is_refused(string kind, string target)
    {
        await Refused(Adapter().PostAsync(Post(kind, target: target), Approval, CancellationToken.None));
    }

    [Fact]
    public async Task Issue_happy_path_appends_the_marker()
    {
        var receipt = await Adapter().PostAsync(Post(), Approval, CancellationToken.None);

        Assert.False(receipt.Reused);
        var item = Assert.Single(_api.Items);
        Assert.Equal("issue", item.Kind);
        Assert.Equal("Crash on startup", item.Title);
        Assert.EndsWith("<!-- bazaar-draft:11111111-2222-3333-4444-555555555555 -->", item.Body);
        Assert.Equal(item.Item.Url, receipt.Url);
    }

    [Fact]
    public async Task Pull_request_goes_through_the_opener_with_marker_head_and_base()
    {
        var receipt = await Adapter().PostAsync(Post("pr"), Approval, CancellationToken.None);

        var request = Assert.Single(_prs.Requests);
        Assert.Equal("agent/fix", request.HeadBranch);
        Assert.Equal("main", request.BaseBranch);
        Assert.Equal(("acme", "widgets"), (request.Owner, request.Repo));
        Assert.EndsWith("<!-- bazaar-draft:11111111-2222-3333-4444-555555555555 -->", request.Body);
        Assert.Equal("3001", receipt.ExternalId);
        Assert.Empty(_api.Items);
    }

    [Fact]
    public async Task Pull_request_opener_failure_propagates()
    {
        _prs.Failure = new CommunityPostException("down", retryable: true);

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Adapter().PostAsync(Post("pr"), Approval, CancellationToken.None));

        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task Pull_request_without_branches_is_refused()
    {
        await Refused(Adapter().PostAsync(Post("pr") with { HeadBranch = null }, Approval, CancellationToken.None));
    }

    [Fact]
    public async Task Comment_happy_path_targets_the_issue_number()
    {
        await Adapter().PostAsync(Post("comment"), Approval, CancellationToken.None);

        var item = Assert.Single(_api.Items);
        Assert.Equal("comment", item.Kind);
        Assert.Equal(7, item.IssueNumber);
    }

    [Fact]
    public async Task Retry_of_the_same_draft_reuses_the_existing_item()
    {
        var adapter = Adapter();
        var first = await adapter.PostAsync(Post(), Approval, CancellationToken.None);

        var second = await adapter.PostAsync(Post(), Approval, CancellationToken.None);

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.ExternalId, second.ExternalId);
        Assert.Single(_api.Items);
    }

    [Fact]
    public async Task Different_drafts_are_not_confused()
    {
        var adapter = Adapter();
        await adapter.PostAsync(Post(), Approval, CancellationToken.None);

        var other = await adapter.PostAsync(Post(draftId: Guid.NewGuid()), Approval, CancellationToken.None);

        Assert.False(other.Reused);
        Assert.Equal(2, _api.Items.Count);
    }

    [Fact]
    public async Task Api_failure_propagates_with_its_retryable_flag()
    {
        _api.NextFailure = new CommunityPostException("rate limited", retryable: true);

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Adapter().PostAsync(Post(), Approval, CancellationToken.None));

        Assert.True(ex.Retryable);
    }

    [Fact]
    public void Component_manifest_is_soft_dependent_on_community()
    {
        var manifest = CommunityGitHubComponent.ManifestDefinition;

        Assert.Equal("community-github", manifest.Id);
        Assert.Empty(manifest.DependsOn);
        Assert.Contains("community", manifest.Soft);
    }

    [Fact]
    public void Registration_resolves_the_adapter_and_keeps_host_overrides()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommunityGitHubTokenSource>(new FixedTokenSource());
        services.AddSingleton<ICommunityPullRequestOpener>(_prs);
        services.AddSingleton<ICommunityFooterCheck>(new RejectingFooterCheck());

        services.AddBazaarCommunityGitHub();
        services.AddBazaarCommunityGitHub();

        using var provider = services.BuildServiceProvider();
        var adapters = provider.GetServices<ICommunityAdapter>().ToArray();
        Assert.IsType<GitHubCommunityAdapter>(Assert.Single(adapters));
        Assert.IsType<RejectingFooterCheck>(provider.GetRequiredService<ICommunityFooterCheck>());
    }

    private sealed class RejectingFooterCheck : ICommunityFooterCheck
    {
        public bool HasDisclosureFooter(string body) => false;
    }
}
