using System.Text.Json;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.GitHub.Auth;
using Bazaar.Server.GitHub.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.GitHub.Tests.Tools;

/// <summary>The per-installation writes-per-hour limit of the agent GitHub tools (doc 21, Agent tools).</summary>
public sealed class GitHubWritesLimitTests
{
    private const string _head = "0123456789abcdef0123456789abcdef01234567";
    private static readonly Guid _agentA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _agentB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Adapter : IGitHubAdapter
    {
        public int Reviews { get; private set; }
        public int Calls { get; private set; }

        public Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<PrFile>>([]);
        }

        public Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken)
        {
            Reviews++;
            return Task.CompletedTask;
        }

        public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_head);
        }

        public Task<PrChecksState> GetChecksStateAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => Task.FromResult(PrChecksState.Passing);

        public Task<RepositoryEventsPage> ListRepositoryEventsAsync(RepositoryEventsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetRepositoryNodeIdAsync(long installationId, string owner, string repo, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult("R_1");
        }
    }

    private sealed class Tokens : IGitHubTokenProvider
    {
        public Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken) => Task.FromResult(new GitHubToken("t", DateTimeOffset.MaxValue));
    }

    private sealed class Installs : IGitHubAgentInstallations
    {
        public Task<long?> GetInstallationIdAsync(Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(agentId == _agentA ? 1 : agentId == _agentB ? 2 : null);
    }

    private static (ServiceProvider Provider, Adapter Adapter, FakeTime Time) Build(int? limit)
    {
        var adapter = new Adapter();
        var time = new FakeTime();
        var values = new Dictionary<string, string?>();
        if (limit is { } l)
        {
            values["Bazaar:GitHub:WritesPerHour"] = l.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IGitHubAdapter>(adapter);
        services.AddSingleton<IGitHubTokenProvider>(new Tokens());
        services.AddSingleton<IGitHubAgentInstallations>(new Installs());
        services.AddBazaarGitHubAgentTools(config);
        return (services.BuildServiceProvider(), adapter, time);
    }

    private static JsonElement ReviewArgs() =>
        JsonElement.Parse($$"""{"owner":"acme","repo":"widgets","number":7,"headSha":"{{_head}}","event":"COMMENT","body":"ok"}""");

    private static JsonElement ReadArgs() => JsonElement.Parse("""{"owner":"acme","repo":"widgets","number":7}""");

    private static async Task<string> Raw(ServiceProvider p, Guid agent) =>
        await p.GetRequiredService<GitHubPrReviewTool>().ExecuteAsync(ReviewArgs(), new ToolCallContext(agent, Guid.NewGuid(), Guid.NewGuid()), TestContext.Current.CancellationToken);

    private static async Task<string> Status(ServiceProvider p, Guid agent) =>
        JsonElement.Parse(await Raw(p, agent)).GetProperty("status").GetString()!;

    [Fact]
    public async Task Writes_under_the_limit_pass_and_the_next_one_is_refused_without_a_GitHub_call()
    {
        var (p, adapter, _) = Build(2);
        await using var provider = p;

        Assert.Equal("applied", await Status(p, _agentA));
        Assert.Equal("applied", await Status(p, _agentA));
        var callsBefore = adapter.Calls;
        var json = await Raw(p, _agentA);

        Assert.Equal("rate_limited", JsonElement.Parse(json).GetProperty("status").GetString());
        Assert.Equal(callsBefore, adapter.Calls);
        Assert.Equal(2, adapter.Reviews);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_window_slides_so_a_slot_frees_after_an_hour()
    {
        var (p, adapter, time) = Build(1);
        await using var provider = p;

        Assert.Equal("applied", await Status(p, _agentA));
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal("rate_limited", await Status(p, _agentA));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("applied", await Status(p, _agentA));
        Assert.Equal(2, adapter.Reviews);
    }

    [Fact]
    public async Task Installations_are_limited_independently()
    {
        var (p, _, _) = Build(1);
        await using var provider = p;

        Assert.Equal("applied", await Status(p, _agentA));
        Assert.Equal("rate_limited", await Status(p, _agentA));
        Assert.Equal("applied", await Status(p, _agentB));
    }

    [Fact]
    public async Task Reads_never_count_against_the_writes_limit()
    {
        var (p, _, _) = Build(1);
        await using var provider = p;
        var read = p.GetRequiredService<GitHubPrReadTool>();

        for (var i = 0; i < 5; i++)
        {
            var json = await read.ExecuteAsync(ReadArgs(), new ToolCallContext(_agentA, Guid.NewGuid(), Guid.NewGuid()), TestContext.Current.CancellationToken);
            Assert.True(JsonElement.Parse(json).GetProperty("ok").GetBoolean());
        }

        Assert.Equal("applied", await Status(p, _agentA));
    }

    [Fact]
    public async Task Calls_refused_before_the_gate_do_not_count()
    {
        var (p, _, _) = Build(1);
        await using var provider = p;

        Assert.Equal("no_installation", await Status(p, Guid.NewGuid()));
        Assert.Equal("applied", await Status(p, _agentA));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100001")]
    public void An_out_of_range_limit_fails_composition(string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:GitHub:WritesPerHour"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddBazaarGitHubAgentTools(config));
    }

    [Fact]
    public void The_default_limit_is_300_per_hour()
    {
        var (p, _, _) = Build(null);
        using var provider = p;
        Assert.Equal(300, p.GetRequiredService<GitHubAgentToolsOptions>().WritesPerHour);
    }
}
