using System.Net;
using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Events;
using Bazaar.GitHub.Ingress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.GitHub.Tests.Ingress;

/// <summary>The Events API poller (doc 21, Ingress), against a scripted HTTP handler and a logical clock.</summary>
public sealed class GitHubEventsPollerTests
{
    private const long _installation = 77;
    private const string _agent = "charles-bazaar[bot]";
    private const string _repoPath = "/repos/acme/bazaar";
    private const string _eventsPath = "/repos/acme/bazaar/events?per_page=100";
    private const string _nodeJson = """{"node_id": "R_kgDOExample1"}""";

    private sealed class RecordingSink : IGitHubEventSink
    {
        public List<GitHubEventDelivery> Deliveries { get; } = [];

        public int FailuresRemaining { get; set; }

        public Task DeliverAsync(GitHubEventDelivery delivery, CancellationToken cancellationToken)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("sink down");
            }

            Deliveries.Add(delivery);
            return Task.CompletedTask;
        }
    }

    private sealed record Harness(
        GitHubEventsPoller Poller,
        ScriptedHttpHandler Http,
        RecordingTimeProvider Time,
        RecordingSink Sink,
        CapturingLogger<GitHubEventsPoller> Logs,
        InMemoryGitHubRateState Rates);

    private static Harness Build(GitHubIngressOptions? ingress = null, IGitHubDeliveryStore? store = null)
    {
        var http = new ScriptedHttpHandler();
        var clock = new RecordingTimeProvider();
        var rates = new InMemoryGitHubRateState();
        var adapterOptions = new GitHubAdapterOptions { BaseAddress = new Uri("https://api.test/"), MaxRetries = 0 };
        var adapter = new GitHubAdapter(new HttpClient(http), new StubTokenProvider("ghs_TESTONLY"), rates, adapterOptions, NullLogger<GitHubAdapter>.Instance, clock);
        return Assemble(adapter, http, clock, rates, ingress, store);
    }

    private static Harness Assemble(GitHubAdapter adapter, ScriptedHttpHandler http, RecordingTimeProvider clock, InMemoryGitHubRateState rates, GitHubIngressOptions? ingress, IGitHubDeliveryStore? store)
    {
        var sink = new RecordingSink();
        var logs = new CapturingLogger<GitHubEventsPoller>();
        var options = ingress ?? Ingress();
        var poller = new GitHubEventsPoller(adapter, sink, store ?? new InMemoryGitHubDeliveryStore(), options, logs, clock);
        return new Harness(poller, http, clock, sink, logs, rates);
    }

    private static GitHubIngressOptions Ingress(int minutes = 1) => new()
    {
        InstallationId = _installation,
        Repositories = ["acme/bazaar"],
        PollIntervalMinutes = minutes,
        AgentLogins = [_agent],
        MemberLogins = ["alice"],
        FailureBackoffBase = TimeSpan.FromSeconds(30),
    };

    private static string Review(string id, string time, string state = "changes_requested", string sha = "sha1") =>
        $$$"""
        {"id": "{{{id}}}", "type": "PullRequestReviewEvent", "actor": {"login": "alice"}, "created_at": "{{{time}}}",
         "payload": {"action": "submitted",
           "review": {"node_id": "PRR_{{{id}}}", "state": "{{{state}}}", "submitted_at": "{{{time}}}", "user": {"login": "alice"}, "author_association": "MEMBER", "body": "please fix", "commit_id": "{{{sha}}}"},
           "pull_request": {"node_id": "PR_1", "number": 1, "head": {"sha": "{{{sha}}}"}, "user": {"login": "{{{_agent}}}"} } } }
        """;

    private static string Page(params string[] events) => "[" + string.Join(",", events) + "]";

    private static void Route(Harness h, params Reply[] eventReplies)
    {
        h.Http.Route(HttpMethod.Get, _repoPath, Reply.Json(HttpStatusCode.OK, _nodeJson));
        h.Http.Route(HttpMethod.Get, _eventsPath, eventReplies);
    }

    private static Reply Ok(string body, string etag = "\"e1\"", params (string, string)[] extra) =>
        Reply.Json(HttpStatusCode.OK, body, [("ETag", etag), .. extra]);

    [Fact]
    public async Task New_events_are_mapped_and_delivered_then_a_304_delivers_nothing()
    {
        var h = Build();
        Route(h, Ok(Page(Review("200", "2026-10-30T11:30:00Z")), "\"e1\""), Reply.NotModified("\"e1\""));

        await h.Poller.PollDueAsync(CancellationToken.None);

        var delivery = Assert.Single(h.Sink.Deliveries);
        Assert.True(delivery.Apply);
        Assert.Equal("github:poll:200", delivery.DedupeKey);
        var effect = Assert.Single(delivery.Effects);
        Assert.True(effect.Wakes);
        Assert.Equal(_agent, Assert.Single(effect.InboxItems).RecipientLogin);

        h.Time.Now += TimeSpan.FromMinutes(2);
        await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Single(h.Sink.Deliveries);
        var events = h.Http.Requests.Where(r => r.Url.Contains("/events", StringComparison.Ordinal)).ToList();
        Assert.Null(events[0].IfNoneMatch);
        Assert.Equal("\"e1\"", events[1].IfNoneMatch);
        Assert.Equal("Bearer ghs_TESTONLY", events[0].Authorization);
    }

    [Fact]
    public async Task Events_are_delivered_oldest_first_whatever_order_github_sent_them()
    {
        var h = Build();
        Route(h, Ok(Page(Review("203", "2026-10-30T11:30:00Z"), Review("201", "2026-10-30T11:10:00Z"), Review("202", "2026-10-30T11:20:00Z"))));

        await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Equal(["201", "202", "203"], h.Sink.Deliveries.Select(d => d.Event.EventId));
    }

    [Fact]
    public async Task The_larger_of_the_configured_interval_and_x_poll_interval_sets_the_next_poll()
    {
        var h = Build();
        Route(h, Ok(Page(), "\"e1\"", ("X-Poll-Interval", "300")), Reply.NotModified("\"e1\""));

        var wait = await h.Poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(300), wait);
        var requestsAfterFirst = h.Http.Requests.Count;

        h.Time.Now += TimeSpan.FromSeconds(299);
        var early = await h.Poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(requestsAfterFirst, h.Http.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), early);

        h.Time.Now += TimeSpan.FromSeconds(1);
        await h.Poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(requestsAfterFirst + 1, h.Http.Requests.Count);
    }

    [Fact]
    public async Task A_short_x_poll_interval_never_beats_the_operators_interval()
    {
        var h = Build(ingress: Ingress(minutes: 10));
        Route(h, Ok(Page(), "\"e1\"", ("X-Poll-Interval", "60")));

        var wait = await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), wait);
    }

    [Fact]
    public async Task An_exhausted_rate_limit_sends_nothing_until_the_reset_time()
    {
        var h = Build();
        var reset = h.Time.Now.AddMinutes(30);
        Route(
            h,
            Ok(Page(), "\"e1\"", ("X-RateLimit-Limit", "5000"), ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))),
            Reply.NotModified("\"e1\""));

        await h.Poller.PollDueAsync(CancellationToken.None);
        var sent = h.Http.Requests.Count;

        h.Time.Now += TimeSpan.FromMinutes(2);
        var wait = await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Equal(sent, h.Http.Requests.Count);
        Assert.Equal(reset - h.Time.Now, wait);
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("rate limited", StringComparison.Ordinal));

        // Past the reset the window is open again: the poll goes out, conditional on the stored ETag.
        h.Time.Now = reset.AddSeconds(1);
        await h.Poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(sent + 1, h.Http.Requests.Count);
        Assert.Equal("\"e1\"", h.Http.Requests[^1].IfNoneMatch);
    }

    [Fact]
    public async Task A_429_from_github_waits_for_the_reset_header()
    {
        var h = Build();
        var reset = h.Time.Now.AddMinutes(20);
        Route(h, Reply.Json(
            HttpStatusCode.TooManyRequests,
            """{"message": "slow down"}""",
            ("X-RateLimit-Remaining", "0"),
            ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))));

        var wait = await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Equal(reset - h.Time.Now, wait);
        Assert.Empty(h.Sink.Deliveries);
    }

    [Fact]
    public async Task Duplicate_event_ids_are_delivered_once_within_a_page_and_across_polls()
    {
        var h = Build();
        var review = Review("300", "2026-10-30T11:30:00Z");
        Route(h, Ok(Page(review, review), "\"e1\""), Ok(Page(Review("301", "2026-10-30T11:40:00Z"), review), "\"e2\""));

        await h.Poller.PollDueAsync(CancellationToken.None);
        h.Time.Now += TimeSpan.FromMinutes(2);
        await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Equal(["300", "301"], h.Sink.Deliveries.Select(d => d.Event.EventId));
    }

    [Fact]
    public async Task An_older_event_arriving_in_a_later_poll_is_stored_but_not_applied()
    {
        var h = Build();
        Route(
            h,
            Ok(Page(Review("401", "2026-10-30T11:40:00Z", sha: "sha2")), "\"e1\""),
            Ok(Page(Review("400", "2026-10-30T11:10:00Z", sha: "sha1")), "\"e2\""));

        await h.Poller.PollDueAsync(CancellationToken.None);
        h.Time.Now += TimeSpan.FromMinutes(2);
        await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Equal(2, h.Sink.Deliveries.Count);
        Assert.True(h.Sink.Deliveries[0].Apply);
        Assert.NotEmpty(h.Sink.Deliveries[0].Effects);
        Assert.False(h.Sink.Deliveries[1].Apply);
        Assert.Empty(h.Sink.Deliveries[1].Effects);
    }

    [Fact]
    public async Task A_server_error_never_throws_and_backs_off_exponentially_then_recovers()
    {
        var h = Build();
        Route(
            h,
            Reply.Json(HttpStatusCode.InternalServerError, "{}"),
            Reply.Json(HttpStatusCode.BadGateway, "{}"),
            Ok(Page(Review("500", "2026-10-30T11:30:00Z"))));

        var first = await h.Poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(30), first);

        h.Time.Now += first;
        var second = await h.Poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(60), second);
        Assert.Empty(h.Sink.Deliveries);

        h.Time.Now += second;
        var third = await h.Poller.PollDueAsync(CancellationToken.None);
        Assert.Single(h.Sink.Deliveries);
        Assert.Equal(TimeSpan.FromMinutes(1), third);
    }

    [Fact]
    public async Task Failure_backoff_is_capped()
    {
        var ingress = new GitHubIngressOptions
        {
            InstallationId = _installation,
            Repositories = ["acme/bazaar"],
            FailureBackoffBase = TimeSpan.FromSeconds(30),
            MaxFailureBackoff = TimeSpan.FromMinutes(2),
        };
        var h = Build(ingress: ingress);
        h.Http.Route(HttpMethod.Get, _repoPath, Reply.Json(HttpStatusCode.OK, _nodeJson));
        var waits = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            h.Http.Route(HttpMethod.Get, _eventsPath, Reply.Json(HttpStatusCode.InternalServerError, "{}"));
            var wait = await h.Poller.PollDueAsync(CancellationToken.None);
            waits.Add(wait);
            h.Time.Now += wait;
        }

        Assert.Equal([30, 60, 120, 120, 120], waits.Select(w => (int)w.TotalSeconds));
    }

    [Fact]
    public async Task The_adapter_retries_a_transient_5xx_inside_one_poll_with_exponential_backoff()
    {
        var http = new ScriptedHttpHandler();
        var clock = new RecordingTimeProvider();
        var rates = new InMemoryGitHubRateState();
        var adapter = new GitHubAdapter(
            new HttpClient(http),
            new StubTokenProvider("ghs_TESTONLY"),
            rates,
            new GitHubAdapterOptions { BaseAddress = new Uri("https://api.test/") },
            NullLogger<GitHubAdapter>.Instance,
            clock);
        var h = Assemble(adapter, http, clock, rates, null, null);
        Route(h, Reply.Json(HttpStatusCode.ServiceUnavailable, "{}"), Reply.Json(HttpStatusCode.ServiceUnavailable, "{}"), Ok(Page(Review("600", "2026-10-30T11:30:00Z"))));

        await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Single(h.Sink.Deliveries);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], h.Time.Delays);
    }

    [Fact]
    public async Task A_sink_failure_leaves_the_event_unprocessed_and_it_is_delivered_again()
    {
        var h = Build();
        h.Sink.FailuresRemaining = 1;
        Route(h, Ok(Page(Review("700", "2026-10-30T11:30:00Z"))), Ok(Page(Review("700", "2026-10-30T11:30:00Z"))));

        var wait = await h.Poller.PollDueAsync(CancellationToken.None);
        Assert.Empty(h.Sink.Deliveries);
        Assert.Equal(TimeSpan.FromSeconds(30), wait);

        h.Time.Now += wait;
        await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Equal("700", Assert.Single(h.Sink.Deliveries).Event.EventId);
        // The first page was not fully handled, so the second request carried no ETag.
        Assert.All(h.Http.Requests.Where(r => r.Url.Contains("/events", StringComparison.Ordinal)), r => Assert.Null(r.IfNoneMatch));
    }

    [Fact]
    public async Task Events_older_than_the_age_cutoff_are_ignored()
    {
        var h = Build();
        Route(h, Ok(Page(Review("800", "2026-10-20T11:30:00Z"), Review("801", "2026-10-30T11:30:00Z"))));

        await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Equal("801", Assert.Single(h.Sink.Deliveries).Event.EventId);
    }

    [Fact]
    public async Task The_repository_node_id_is_looked_up_once_and_stamped_on_events()
    {
        var h = Build();
        Route(h, Ok(Page(Review("900", "2026-10-30T11:30:00Z")), "\"e1\""), Reply.NotModified("\"e1\""));

        await h.Poller.PollDueAsync(CancellationToken.None);
        h.Time.Now += TimeSpan.FromMinutes(2);
        await h.Poller.PollDueAsync(CancellationToken.None);

        Assert.Equal("R_kgDOExample1", h.Sink.Deliveries[0].Event.RepoNodeId);
        Assert.Single(h.Http.Requests, r => r.Url.EndsWith("/repos/acme/bazaar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_is_the_only_exception_that_leaves_the_poller()
    {
        var h = Build();
        Route(h, Ok(Page()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Poller.PollDueAsync(cts.Token));
    }
}
