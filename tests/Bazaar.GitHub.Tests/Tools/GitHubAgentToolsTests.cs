using System.Text.Json;
using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.GitHub.Tools;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.GitHub.Tests.Tools;

/// <summary>The agent GitHub tools (doc 21, Agent tools [PROPOSED]).</summary>
public sealed class GitHubAgentToolsTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private static readonly Guid _agent = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class FakeAdapter : IGitHubAdapter
    {
        public string CurrentHead { get; set; } = Head;
        public bool RepoVisible { get; set; } = true;
        public List<(PrRef Pr, ReviewRequest Review)> Reviews { get; } = [];
        public List<long> InstallationsSeen { get; } = [];

        public Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("The tools must never open PRs.");

        public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
        {
            InstallationsSeen.Add(pr.InstallationId);
            return Task.FromResult<IReadOnlyList<PrFile>>([new PrFile("a.cs", "modified", 1, 0, new string('x', 9000))]);
        }

        public Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken)
        {
            Reviews.Add((pr, review));
            return Task.CompletedTask;
        }

        public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken) => Task.FromResult(CurrentHead);

        public Task<PrChecksState> GetChecksStateAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => Task.FromResult(PrChecksState.Failing);

        public Task<RepositoryEventsPage> ListRepositoryEventsAsync(RepositoryEventsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetRepositoryNodeIdAsync(long installationId, string owner, string repo, CancellationToken cancellationToken) =>
            RepoVisible ? Task.FromResult("R_1") : throw new GitHubApiException(System.Net.HttpStatusCode.NotFound, "Not Found", "not found");
    }

    private sealed class Tokens : IGitHubTokenProvider
    {
        public Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken) => Task.FromResult(new GitHubToken("t", DateTimeOffset.MaxValue));
    }

    private sealed class Installs(long? id) : IGitHubAgentInstallations
    {
        public List<Guid> Asked { get; } = [];

        public Task<long?> GetInstallationIdAsync(Guid agentId, CancellationToken cancellationToken)
        {
            Asked.Add(agentId);
            return Task.FromResult(id);
        }
    }

    private static JsonElement Json(string text) => JsonElement.Parse(text);

    private static ToolCallContext Ctx(Guid? agent = null) => new(agent ?? _agent, Guid.NewGuid(), Guid.NewGuid());

    private static string ReviewArgs(string ev = "COMMENT", string head = Head, string body = "ok") =>
        $$"""{"owner":"acme","repo":"widgets","number":7,"headSha":"{{head}}","event":"{{ev}}","body":"{{body}}"}""";

    private static (GitHubPrReviewTool Tool, FakeAdapter Adapter, Installs Installs) Review(long? installation = 42, IGitHubTokenProvider? tokens = null)
    {
        var adapter = new FakeAdapter();
        var installs = new Installs(installation);
        return (new GitHubPrReviewTool(adapter, tokens ?? new Tokens(), installs), adapter, installs);
    }

    [Fact]
    public async Task Review_posts_a_comment_under_the_installation_of_the_calling_agent()
    {
        var (tool, adapter, installs) = Review();

        var result = Json(await tool.ExecuteAsync(Json(ReviewArgs()), Ctx(), TestContext.Current.CancellationToken));

        Assert.True(result.GetProperty("ok").GetBoolean());
        var posted = Assert.Single(adapter.Reviews);
        Assert.Equal(42, posted.Pr.InstallationId);
        Assert.Equal("COMMENT", posted.Review.Event);
        Assert.Equal([_agent], installs.Asked);
    }

    [Fact]
    public async Task Identity_comes_from_the_context_and_an_agentId_argument_is_ignored()
    {
        var (tool, _, installs) = Review();
        var other = Guid.NewGuid();
        var args = ReviewArgs().Replace("{\"owner\"", $"{{\"agentId\":\"{Guid.NewGuid()}\",\"installationId\":999,\"owner\"");

        await tool.ExecuteAsync(Json(args), Ctx(other), TestContext.Current.CancellationToken);

        Assert.Equal([other], installs.Asked);
    }

    [Theory]
    [InlineData("APPROVE")]
    [InlineData("approve")]
    [InlineData("MERGE")]
    [InlineData("")]
    public async Task Approve_and_any_other_event_is_refused_before_github_is_touched(string ev)
    {
        var (tool, adapter, installs) = Review();

        var result = Json(await tool.ExecuteAsync(Json(ReviewArgs(ev)), Ctx(), TestContext.Current.CancellationToken));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("refused", result.GetProperty("status").GetString());
        Assert.Empty(adapter.Reviews);
        Assert.Empty(installs.Asked);
    }

    [Fact]
    public async Task A_moved_head_is_stale_and_nothing_is_posted()
    {
        var (tool, adapter, _) = Review();
        adapter.CurrentHead = new string('a', 40);

        var result = Json(await tool.ExecuteAsync(Json(ReviewArgs()), Ctx(), TestContext.Current.CancellationToken));

        Assert.Equal("stale", result.GetProperty("status").GetString());
        Assert.Empty(adapter.Reviews);
    }

    [Theory]
    [InlineData("""{"owner":"../x","repo":"r","number":1,"headSha":"0123456789abcdef0123456789abcdef01234567","event":"COMMENT","body":"b"}""")]
    [InlineData("""{"owner":"a","repo":"..","number":1,"headSha":"0123456789abcdef0123456789abcdef01234567","event":"COMMENT","body":"b"}""")]
    [InlineData("""{"owner":"a","repo":"r/../../x","number":1,"headSha":"0123456789abcdef0123456789abcdef01234567","event":"COMMENT","body":"b"}""")]
    [InlineData("""{"owner":"a","repo":"r","number":0,"headSha":"0123456789abcdef0123456789abcdef01234567","event":"COMMENT","body":"b"}""")]
    [InlineData("""{"owner":"a","repo":"r","number":1,"headSha":"main","event":"COMMENT","body":"b"}""")]
    [InlineData("""{"owner":"a","repo":"r","number":1,"headSha":"0123456789abcdef0123456789abcdef01234567","event":"COMMENT","body":""}""")]
    [InlineData("""[1]""")]
    public async Task Invalid_arguments_are_refused_without_a_lookup(string args)
    {
        var (tool, adapter, installs) = Review();

        var result = Json(await tool.ExecuteAsync(Json(args), Ctx(), TestContext.Current.CancellationToken));

        Assert.Equal("invalid_arguments", result.GetProperty("status").GetString());
        Assert.Empty(adapter.Reviews);
        Assert.Empty(installs.Asked);
    }

    [Fact]
    public async Task An_oversized_body_is_refused()
    {
        var (tool, adapter, _) = Review();

        var result = Json(await tool.ExecuteAsync(Json(ReviewArgs(body: new string('x', 20_001))), Ctx(), TestContext.Current.CancellationToken));

        Assert.Equal("invalid_arguments", result.GetProperty("status").GetString());
        Assert.Empty(adapter.Reviews);
    }

    [Fact]
    public async Task An_agent_without_an_active_installation_is_refused()
    {
        var (tool, adapter, _) = Review(installation: null);

        var result = Json(await tool.ExecuteAsync(Json(ReviewArgs()), Ctx(), TestContext.Current.CancellationToken));

        Assert.Equal("no_installation", result.GetProperty("status").GetString());
        Assert.Empty(adapter.Reviews);
    }

    [Fact]
    public async Task A_repository_the_app_cannot_see_is_refused()
    {
        var (tool, adapter, _) = Review();
        adapter.RepoVisible = false;

        var result = Json(await tool.ExecuteAsync(Json(ReviewArgs()), Ctx(), TestContext.Current.CancellationToken));

        Assert.Equal("repo_not_accessible", result.GetProperty("status").GetString());
        Assert.Empty(adapter.Reviews);
    }

    [Fact]
    public async Task Without_a_token_source_both_tools_fail_closed_with_no_secrets()
    {
        var (review, adapter, _) = Review(tokens: new UnconfiguredGitHubTokenProvider());
        var read = new GitHubPrReadTool(adapter, new UnconfiguredGitHubTokenProvider(), new Installs(42));

        var a = await review.ExecuteAsync(Json(ReviewArgs()), Ctx(), TestContext.Current.CancellationToken);
        var b = await read.ExecuteAsync(Json("""{"owner":"acme","repo":"widgets","number":7}"""), Ctx(), TestContext.Current.CancellationToken);

        Assert.Equal("not_configured", Json(a).GetProperty("status").GetString());
        Assert.Equal("not_configured", Json(b).GetProperty("status").GetString());
        Assert.Empty(adapter.Reviews);
    }

    [Fact]
    public async Task Read_returns_head_checks_and_truncated_patches_for_the_agents_installation()
    {
        var adapter = new FakeAdapter();
        var tool = new GitHubPrReadTool(adapter, new Tokens(), new Installs(42));

        var result = Json(await tool.ExecuteAsync(Json("""{"owner":"acme","repo":"widgets","number":7}"""), Ctx(), TestContext.Current.CancellationToken));

        Assert.Equal(Head, result.GetProperty("headSha").GetString());
        Assert.Equal("failing", result.GetProperty("checks").GetString());
        var file = result.GetProperty("files")[0];
        Assert.True(file.GetProperty("patchTruncated").GetBoolean());
        Assert.Equal(8000, file.GetProperty("patch").GetString()!.Length);
        Assert.Equal([42L], adapter.InstallationsSeen);
    }

    [Fact]
    public async Task The_plain_execute_overload_throws_so_a_call_cannot_bypass_the_dispatcher()
    {
        var (tool, _, _) = Review();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ((ITool)tool).ExecuteAsync(Json(ReviewArgs()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_module_registers_only_read_and_review_tools_with_policy_and_no_merge_or_open_path()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ConfigurationBuilder().Build());
        services.AddSingleton<IGitHubAdapter>(new FakeAdapter());
        services.AddSingleton<IGitHubTokenProvider>(new Tokens());
        services.AddBazaarGitHubAgentTools();
        using var provider = services.BuildServiceProvider();

        var names = provider.GetServices<ToolRegistration>().Select(r => r.Name).Order().ToArray();
        var capabilities = provider.GetServices<ToolCapability>().ToDictionary(c => c.Name);

        Assert.Equal(["github.pr_read", "github.pr_review"], names);
        Assert.Equal(names, provider.GetServices<Bazaar.Contracts.Gateway.ModelToolDefinition>().Select(d => d.Name).Order());
        Assert.Equal(ToolTier.Autonomous, capabilities["github.pr_read"].Tier);
        Assert.Equal(ToolTier.AutonomousWithNotice, capabilities["github.pr_review"].Tier);
        Assert.NotNull(capabilities["github.pr_review"].Rate);
        Assert.DoesNotContain(names, n => n.Contains("merge", StringComparison.Ordinal) || n.Contains("open", StringComparison.Ordinal));
        Assert.All(provider.GetServices<ToolRegistration>(), r => Assert.IsAssignableFrom<IContextualTool>(r.Tool));
    }

    [Fact]
    public void The_component_adds_the_tools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new GitHubComponent().AddServices(services, new ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(ToolRegistration));
    }
}
