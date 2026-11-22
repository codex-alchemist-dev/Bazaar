using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Client.GitSolarSystem.Http;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.GitSolarSystem.Tests.Http;

public class HttpGitSolarSourceTests
{
    private const string _repository = "github/codex-alchemist-dev/Bazaar";
    private const string _path = "/git/github/repos/codex-alchemist-dev/Bazaar/solar";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Records every wait. After <paramref name="stopAfter"/> waits it cancels the enumeration.</summary>
    private sealed class FakeDelay(int stopAfter, CancellationTokenSource stop)
    {
        public List<TimeSpan> Waits { get; } = [];

        public Task Wait(TimeSpan wait, CancellationToken cancellationToken)
        {
            Waits.Add(wait);
            if (Waits.Count >= stopAfter) stop.Cancel();
            return Task.CompletedTask;
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string FeedJson(GitSolarSnapshot snapshot, LayoutFrame frame) =>
        JsonSerializer.Serialize(new GitSolarFeed(snapshot, frame), WireJsonContext.Default.GitSolarFeed);

    private static string FeedJson(long version) =>
        FeedJson(GitSolarFixtures.MergeWithComet(version), GitSolarFixtures.Frame(version, 1, 0f, 0f, 0f, 1f, 2f, 0f, 2f, 0f, 0f));

    private static HttpGitSolarSource Source(
        HttpMessageHandler handler,
        FakeDelay delay,
        string? token = "secret",
        TimeSpan? pollInterval = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080/") };
        return new HttpGitSolarSource(http, () => token, pollInterval, delay.Wait);
    }

    private static async Task<List<GitSolarUpdate>> Collect(HttpGitSolarSource source, CancellationToken cancellationToken)
    {
        var updates = new List<GitSolarUpdate>();
        await foreach (var update in source.UpdatesAsync(_repository, cancellationToken))
        {
            updates.Add(update);
        }

        return updates;
    }

    [Fact]
    public async Task Success_yields_a_snapshot_then_a_frame_from_the_solar_route_with_the_bearer_token()
    {
        var handler = new FakeHandler(_ => Json(FeedJson(7)));
        using var stop = new CancellationTokenSource();
        var delay = new FakeDelay(stopAfter: 1, stop);

        var updates = await Collect(Source(handler, delay), stop.Token);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(_path, request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("secret", request.Headers.Authorization.Parameter);

        Assert.Equal(2, updates.Count);
        var snapshot = Assert.IsType<GitSolarUpdate.Snapshot>(updates[0]).Value;
        Assert.Equal(7, snapshot.GraphVersion);
        Assert.Equal(3, snapshot.Commits.Count);
        var frame = Assert.IsType<GitSolarUpdate.Frame>(updates[1]).Value;
        Assert.Equal(7, frame.GraphVersion);
        Assert.Equal(new float[] { 0f, 0f, 0f, 1f, 2f, 0f, 2f, 0f, 0f }, frame.Xyz.ToArray());
    }

    [Fact]
    public async Task Without_a_credential_sends_no_authorization_header()
    {
        var handler = new FakeHandler(_ => Json(FeedJson(1)));
        using var stop = new CancellationTokenSource();

        await Collect(Source(handler, new FakeDelay(1, stop), token: null), stop.Token);

        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task Malformed_json_yields_disconnected_and_backs_off()
    {
        var handler = new FakeHandler(_ => Json("{ not json"));
        using var stop = new CancellationTokenSource();
        var delay = new FakeDelay(stopAfter: 1, stop);

        var updates = await Collect(Source(handler, delay), stop.Token);

        Assert.Single(updates);
        Assert.IsType<GitSolarUpdate.Disconnected>(updates[0]);
        Assert.Equal(new[] { HttpGitSolarSource.InitialBackoff }, delay.Waits.ToArray());
    }

    [Fact]
    public async Task A_body_without_a_snapshot_is_malformed()
    {
        var handler = new FakeHandler(_ => Json("{}"));
        using var stop = new CancellationTokenSource();

        var updates = await Collect(Source(handler, new FakeDelay(1, stop)), stop.Token);

        Assert.IsType<GitSolarUpdate.Disconnected>(Assert.Single(updates));
    }

    [Fact]
    public async Task A_404_yields_disconnected_and_waits_one_poll_interval_without_backoff()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var stop = new CancellationTokenSource();
        var delay = new FakeDelay(stopAfter: 3, stop);
        var interval = TimeSpan.FromSeconds(9);

        var updates = await Collect(Source(handler, delay, pollInterval: interval), stop.Token);

        Assert.All(updates, update => Assert.IsType<GitSolarUpdate.Disconnected>(update));
        Assert.Equal(3, updates.Count);
        Assert.Equal(new[] { interval, interval, interval }, delay.Waits.ToArray());
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_409_is_treated_like_a_404_with_no_hot_loop()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict));
        using var stop = new CancellationTokenSource();
        var delay = new FakeDelay(stopAfter: 2, stop);

        var updates = await Collect(Source(handler, delay), stop.Token);

        Assert.All(updates, update => Assert.IsType<GitSolarUpdate.Disconnected>(update));
        Assert.Equal(HttpGitSolarSource.DefaultPollInterval, delay.Waits[0]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Repeated_server_errors_back_off_exponentially_up_to_the_cap()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var stop = new CancellationTokenSource();
        var delay = new FakeDelay(stopAfter: 8, stop);

        await Collect(Source(handler, delay), stop.Token);

        Assert.Equal(
            new[] { 1, 2, 4, 8, 16, 30, 30, 30 }.Select(seconds => TimeSpan.FromSeconds(seconds)).ToArray(),
            delay.Waits.ToArray());
    }

    [Fact]
    public async Task A_transport_failure_backs_off_and_a_success_resets_the_backoff()
    {
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            calls++;
            return calls switch
            {
                1 or 2 => throw new HttpRequestException("connection refused"),
                _ => Json(FeedJson(3)),
            };
        });
        using var stop = new CancellationTokenSource();
        var delay = new FakeDelay(stopAfter: 4, stop);

        var updates = await Collect(Source(handler, delay), stop.Token);

        // Two failures back off 1 s then 2 s; the success waits the poll interval; the next poll returns the same version.
        Assert.Equal(TimeSpan.FromSeconds(1), delay.Waits[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), delay.Waits[1]);
        Assert.Equal(HttpGitSolarSource.DefaultPollInterval, delay.Waits[2]);
        Assert.Equal(TimeSpan.FromSeconds(5), delay.Waits[3]);
        Assert.Equal(2, updates.Count(update => update is GitSolarUpdate.Disconnected));
    }

    [Fact]
    public async Task Yields_a_snapshot_only_when_the_graph_version_increases()
    {
        var versions = new Queue<long>([5, 5, 6, 6, 4]);
        var handler = new FakeHandler(_ => Json(FeedJson(versions.Dequeue())));
        using var stop = new CancellationTokenSource();
        var delay = new FakeDelay(stopAfter: 5, stop);

        var updates = await Collect(Source(handler, delay), stop.Token);

        var snapshots = updates.OfType<GitSolarUpdate.Snapshot>().Select(update => update.Value.GraphVersion).ToArray();
        Assert.Equal(new long[] { 5, 6 }, snapshots);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task Yields_a_frame_only_when_its_version_or_layout_sequence_changes()
    {
        var snapshot = GitSolarFixtures.MergeWithComet(1);
        var frames = new Queue<LayoutFrame>(new[]
        {
            GitSolarFixtures.Frame(1, 1, 0f, 0f, 0f, 1f, 1f, 1f, 2f, 2f, 2f),
            GitSolarFixtures.Frame(1, 1, 0f, 0f, 0f, 1f, 1f, 1f, 2f, 2f, 2f),
            GitSolarFixtures.Frame(1, 2, 0f, 0f, 0f, 1f, 1f, 1f, 2f, 2f, 2f),
        });
        var handler = new FakeHandler(_ => Json(FeedJson(snapshot, frames.Dequeue())));
        using var stop = new CancellationTokenSource();

        var updates = await Collect(Source(handler, new FakeDelay(3, stop)), stop.Token);

        Assert.Single(updates.OfType<GitSolarUpdate.Snapshot>());
        Assert.Equal(2, updates.OfType<GitSolarUpdate.Frame>().Count());
    }

    [Theory]
    [InlineData("github/owner")]
    [InlineData("github//repo")]
    [InlineData("github/owner/repo/extra")]
    public async Task A_repository_that_is_not_provider_owner_repo_yields_one_disconnected_and_no_request(string repository)
    {
        var handler = new FakeHandler(_ => Json(FeedJson(1)));
        using var stop = new CancellationTokenSource();
        var source = Source(handler, new FakeDelay(1, stop));

        var updates = new List<GitSolarUpdate>();
        await foreach (var update in source.UpdatesAsync(repository, stop.Token))
        {
            updates.Add(update);
        }

        Assert.IsType<GitSolarUpdate.Disconnected>(Assert.Single(updates));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_ends_the_enumeration_without_throwing()
    {
        var handler = new FakeHandler(_ => Json(FeedJson(1)));
        using var stop = new CancellationTokenSource();
        var source = Source(handler, new FakeDelay(2, stop));

        var updates = await Collect(source, stop.Token);

        Assert.NotEmpty(updates);
        Assert.True(stop.IsCancellationRequested);
    }
}
