using System.Net;
using Bazaar.Approvals;
using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Events;
using Bazaar.GitHub.Ingress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.GitHub.Tests.Ingress;

public sealed class GitHubEventsParserAndServiceTests
{
    private static readonly IReadOnlySet<string> _members = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alice" };

    private static GitHubEventsApiParser.Result Parse(string body) => GitHubEventsApiParser.Parse(body, "R_node", _members);

    [Fact]
    public void A_pull_request_closed_event_carries_pr_state()
    {
        var result = Parse(
            """
            [{"id": "1", "type": "PullRequestEvent", "actor": {"login": "alice"}, "created_at": "2026-10-30T10:00:00Z",
              "payload": {"action": "closed", "pull_request": {"node_id": "PR_9", "merged": true, "updated_at": "2026-10-30T10:00:01Z",
                "head": {"sha": "abc"}, "user": {"login": "alice"}, "author_association": "MEMBER", "body": "b"}}}]
            """);

        var e = Assert.Single(result.Events);
        Assert.Equal(("pull_request", "closed", "PR_9", "abc", true), (e.EventName, e.Action, e.PrNodeId, e.HeadSha, e.Merged));
        Assert.Equal(GitHubEventSource.Poll, e.Source);
        Assert.Equal("R_node", e.RepoNodeId);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 10, 0, 1, TimeSpan.Zero), e.UpdatedAt);
    }

    [Fact]
    public void A_comment_on_a_pull_request_targets_the_pr_node_and_a_plain_issue_comment_the_issue()
    {
        var result = Parse(
            """
            [{"id": "2", "type": "IssueCommentEvent", "actor": {"login": "alice"}, "created_at": "2026-10-30T10:00:00Z",
              "payload": {"action": "created", "comment": {"node_id": "IC_1", "body": "hi @charles-bazaar[bot]", "updated_at": "2026-10-30T10:00:00Z"},
                "issue": {"node_id": "PR_9", "pull_request": {"url": "u"}}}},
             {"id": "3", "type": "IssueCommentEvent", "actor": {"login": "alice"}, "created_at": "2026-10-30T10:01:00Z",
              "payload": {"action": "created", "comment": {"node_id": "IC_2", "body": "x", "updated_at": "2026-10-30T10:01:00Z"},
                "issue": {"node_id": "I_5"}}}]
            """);

        Assert.Equal("PR_9", result.Events[0].PrNodeId);
        Assert.Null(result.Events[0].IssueNodeId);
        Assert.Null(result.Events[1].PrNodeId);
        Assert.Equal("I_5", result.Events[1].IssueNodeId);
    }

    [Fact]
    public void Association_fails_closed_unless_the_actor_is_a_known_member_or_the_author_of_the_object()
    {
        var result = Parse(
            """
            [{"id": "4", "type": "IssuesEvent", "actor": {"login": "mallory"}, "created_at": "2026-10-30T10:00:00Z",
              "payload": {"action": "opened", "issue": {"node_id": "I_1", "user": {"login": "someone-else"}, "author_association": "OWNER", "updated_at": "2026-10-30T10:00:00Z"}}},
             {"id": "5", "type": "IssuesEvent", "actor": {"login": "bob"}, "created_at": "2026-10-30T10:00:00Z",
              "payload": {"action": "opened", "issue": {"node_id": "I_2", "user": {"login": "bob"}, "author_association": "CONTRIBUTOR", "updated_at": "2026-10-30T10:00:00Z"}}},
             {"id": "6", "type": "IssuesEvent", "actor": {"login": "Alice"}, "created_at": "2026-10-30T10:00:00Z",
              "payload": {"action": "opened", "issue": {"node_id": "I_3", "user": {"login": "Alice"}, "updated_at": "2026-10-30T10:00:00Z"}}}]
            """);

        Assert.Equal(["NONE", "CONTRIBUTOR", "MEMBER"], result.Events.Select(e => e.AuthorAssociation));
        Assert.False(GitHubEventMapper.IsTrustedAssociation(result.Events[0].AuthorAssociation));
    }

    [Fact]
    public void Unsupported_types_are_skipped_and_malformed_events_are_counted()
    {
        var result = Parse(
            """
            [{"id": "7", "type": "WatchEvent", "actor": {"login": "a"}, "created_at": "2026-10-30T10:00:00Z", "payload": {}},
             {"id": "8", "type": "PushEvent", "actor": {"login": "a"}, "created_at": "2026-10-30T10:00:00Z", "payload": {"head": "deadbeef"}},
             {"type": "PushEvent", "actor": {"login": "a"}, "created_at": "2026-10-30T10:00:00Z", "payload": {}},
             {"id": "9", "type": "PushEvent", "created_at": "2026-10-30T10:00:00Z", "payload": {}}]
            """);

        var push = Assert.Single(result.Events);
        Assert.Equal(("push", "deadbeef", null), (push.EventName, push.HeadSha, push.Action));
        Assert.Equal(2, result.Malformed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void A_body_that_is_not_an_event_array_is_an_api_error(string body) =>
        Assert.Throws<GitHubApiException>(() => Parse(body));

    [Fact]
    public async Task The_service_stays_off_without_an_installation_or_repositories_and_logs_once()
    {
        var logs = new CapturingLogger<GitHubEventsPollingService>();
        var http = new ScriptedHttpHandler();
        var clock = new RecordingTimeProvider();
        var sink = new NullSink();
        var options = new GitHubIngressOptions();
        var adapter = new GitHubAdapter(new HttpClient(http), new StubTokenProvider("t"), new InMemoryGitHubRateState(), new GitHubAdapterOptions { BaseAddress = new Uri("https://api.test/") }, NullLogger<GitHubAdapter>.Instance, clock);
        var poller = new GitHubEventsPoller(adapter, sink, new InMemoryGitHubDeliveryStore(), options, NullLogger<GitHubEventsPoller>.Instance, clock);
        var service = new GitHubEventsPollingService(poller, options, sink, logs, clock);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains("installation id", service.DisabledReason, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
        var line = Assert.Single(logs.Entries);
        Assert.Contains("disabled", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_service_stays_off_while_the_sink_is_the_unwired_default()
    {
        var logs = new CapturingLogger<GitHubEventsPollingService>();
        var http = new ScriptedHttpHandler();
        var clock = new RecordingTimeProvider();
        var sink = new UnwiredGitHubEventSink();
        var options = new GitHubIngressOptions { InstallationId = 5, Repositories = ["acme/bazaar"] };
        var adapter = new GitHubAdapter(new HttpClient(http), new StubTokenProvider("t"), new InMemoryGitHubRateState(), new GitHubAdapterOptions { BaseAddress = new Uri("https://api.test/") }, NullLogger<GitHubAdapter>.Instance, clock);
        var poller = new GitHubEventsPoller(adapter, sink, new InMemoryGitHubDeliveryStore(), options, NullLogger<GitHubEventsPoller>.Instance, clock);
        var service = new GitHubEventsPollingService(poller, options, sink, logs, clock);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains("sink", service.DisabledReason, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_configured_service_polls_then_sleeps_and_stops_on_cancellation()
    {
        var http = new ScriptedHttpHandler();
        http.Route(HttpMethod.Get, "/repos/acme/bazaar", Reply.Json(HttpStatusCode.OK, """{"node_id": "R_1"}"""));
        http.Route(HttpMethod.Get, "/repos/acme/bazaar/events?per_page=100", Reply.Json(HttpStatusCode.OK, "[]", ("ETag", "\"x\"")));
        var sink = new NullSink();
        var options = new GitHubIngressOptions { InstallationId = 5, Repositories = ["acme/bazaar"] };
        var adapter = new GitHubAdapter(new HttpClient(http), new StubTokenProvider("t"), new InMemoryGitHubRateState(), new GitHubAdapterOptions { BaseAddress = new Uri("https://api.test/") }, NullLogger<GitHubAdapter>.Instance);
        var poller = new GitHubEventsPoller(adapter, sink, new InMemoryGitHubDeliveryStore(), options, NullLogger<GitHubEventsPoller>.Instance);
        var service = new GitHubEventsPollingService(poller, options, sink, NullLogger<GitHubEventsPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        for (var i = 0; i < 200 && http.Requests.Count < 2; i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Null(service.DisabledReason);
        Assert.Equal(2, http.Requests.Count);

        await service.StopAsync(CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(2, http.Requests.Count);
    }

    private sealed class NullSink : IGitHubEventSink
    {
        public Task DeliverAsync(GitHubEventDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoKeys : IOwnerKeyRegistry
    {
        public Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult<OwnerDeviceKey?>(null);
    }

    [Fact]
    public async Task Registration_adds_the_poller_as_a_hosted_service_that_is_disabled_by_default()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOwnerKeyRegistry, NoKeys>();
        services.AddSingleton<IOwnerNonceStore, InMemoryOwnerNonceStore>();
        services.AddSingleton<IOwnerSignatureVerifier, OwnerSignatureVerifier>();
        services.AddBazaarGitHub(configuration);
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        var hosted = provider.GetServices<IHostedService>().OfType<GitHubEventsPollingService>().Single();

        Assert.NotNull(hosted.DisabledReason);
        Assert.IsType<UnwiredGitHubEventSink>(provider.GetRequiredService<IGitHubEventSink>());
        Assert.IsType<InMemoryGitHubDeliveryStore>(provider.GetRequiredService<IGitHubDeliveryStore>());
    }

    [Fact]
    public async Task Ingress_options_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:GitHub:Ingress:InstallationId"] = "42",
            ["Bazaar:GitHub:Ingress:Repositories:0"] = "acme/bazaar",
            ["Bazaar:GitHub:Ingress:PollIntervalMinutes"] = "5",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBazaarGitHub(configuration);
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<GitHubIngressOptions>();

        Assert.Equal(42, options.InstallationId);
        Assert.Equal(5, options.PollIntervalMinutes);
        Assert.Null(options.DisabledReason());
        Assert.Equal([("acme", "bazaar")], options.ParsedRepositories());
    }
}
