using System.Net;
using Bazaar.Server.WebAdmin;

namespace Bazaar.StatusTui.Tests;

public sealed class StatusScreenTests
{
    private static readonly AdminStatusView _view = new(
        "Bazaar", "0.1.0+a1b2c3d", null, new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero), 93784,
        ["os: Linux", "service host: systemd"],
        [new AdminQueueDepth("jobs", 3), new AdminQueueDepth("mail", 0)],
        new AdminHealthSummary("degraded", [new AdminHealthCheck("db", "healthy", null), new AdminHealthCheck("mail", "degraded", "slow sends")]));

    [Fact]
    public void Snapshot_of_the_status_screen()
    {
        const string expected = """
            Bazaar  0.1.0+a1b2c3d
            =====================
            Up        1d 2h 3m
            Started   2026-11-22 10:00 UTC
            Health    degraded
              healthy  db
              degraded mail: slow sends

            Queues
              jobs  3
              mail  0

            Providers and host
              os: Linux
              service host: systemd
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n") + "\n", StatusScreen.Render(_view).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Missing_queues_and_health_say_so_instead_of_inventing_values()
    {
        var text = StatusScreen.Render(_view with { Queues = null, Health = null });
        Assert.Contains("Health    unknown", text);
        Assert.Contains("none reported", text);
    }

    [Fact]
    public void Escape_sequences_from_the_server_are_neutralised()
    {
        var text = StatusScreen.Render(_view with { ProviderLines = ["\u001b[2Jwipe"] });
        Assert.DoesNotContain('\u001b', text);
        Assert.Contains("?[2Jwipe", text);
    }

    [Fact]
    public void Parse_reads_the_panel_envelope_and_the_same_json_the_web_page_gets()
    {
        const string body = """{"available":true,"data":{"displayName":"Bazaar","version":"0.1.0","codename":null,"startedAt":"2026-11-22T10:00:00+00:00","uptimeSeconds":5,"providerLines":["p1"],"queues":[{"name":"jobs","depth":2}],"health":{"state":"healthy","checks":[]}}}""";
        var v = StatusJson.Parse(body);
        Assert.NotNull(v);
        Assert.Equal(2, v.Queues![0].Depth);
        Assert.Equal("healthy", v.Health!.State);
    }

    [Theory]
    [InlineData("""{"available":false}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void Parse_returns_null_for_unavailable_or_bad_bodies(string body) => Assert.Null(StatusJson.Parse(body));

    private sealed class Handler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Fetch_sends_the_cookie_to_the_status_route_and_returns_the_view()
    {
        var h = new Handler(HttpStatusCode.OK, """{"available":true,"data":{"displayName":"B","version":"1","startedAt":"2026-11-22T10:00:00+00:00","uptimeSeconds":1,"providerLines":[]}}""");
        var (view, error) = await StatusClient.FetchAsync(new HttpClient(h), new Uri("http://127.0.0.1:5000"), "bazaar-admin", "abc", TestContext.Current.CancellationToken);
        Assert.Null(error);
        Assert.Equal("B", view!.DisplayName);
        Assert.Equal("/admin/api/status", h.Seen!.RequestUri!.AbsolutePath);
        Assert.Equal("bazaar-admin=abc", Assert.Single(h.Seen.Headers.GetValues("Cookie")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Not signed in")]
    [InlineData(HttpStatusCode.Forbidden, "may not read")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task Fetch_explains_failures_without_echoing_the_cookie(HttpStatusCode code, string expected)
    {
        var (view, error) = await StatusClient.FetchAsync(new HttpClient(new Handler(code, "")), new Uri("http://x"), "bazaar-admin", "s3cret", TestContext.Current.CancellationToken);
        Assert.Null(view);
        Assert.Contains(expected, error);
        Assert.DoesNotContain("s3cret", error);
    }
}
