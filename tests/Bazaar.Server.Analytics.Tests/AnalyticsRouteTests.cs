using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Analytics.Tests;

public class AnalyticsRouteTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

    private static async Task<WebApplication> StartAsync(Func<HttpContext, Task<AnalyticsResolution>> resolve, AnalyticsVisibility v = AnalyticsVisibility.OwnerAndSelf)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IWorkEventSource>(new Source());
        builder.Services.AddSingleton(new AnalyticsOptions(v));
        builder.Services.AddSingleton<IAgentAnalytics, AgentAnalyticsService>();
        var app = builder.Build();
        app.MapBazaarAnalytics(resolve, new FixedClock());
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Failure_from_the_resolver_is_sent_unchanged()
    {
        await using var app = await StartAsync(_ => Task.FromResult(new AnalyticsResolution(null, Results.Json(new { error = "rate_limited" }, statusCode: 429))));

        var r = await app.GetTestClient().GetAsync("/analytics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
    }

    [Fact]
    public async Task No_viewer_and_no_failure_is_401()
    {
        await using var app = await StartAsync(_ => Task.FromResult(new AnalyticsResolution(null)));

        var r = await app.GetTestClient().GetAsync("/analytics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    public async Task Days_out_of_range_is_400(string days)
    {
        await using var app = await StartAsync(_ => Task.FromResult(new AnalyticsResolution(new AnalyticsViewer("o", true, false))));

        var r = await app.GetTestClient().GetAsync("/analytics?days=" + days, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Owner_gets_the_pane_with_every_agent_and_an_agent_only_itself()
    {
        await using var app = await StartAsync(ctx => Task.FromResult(new AnalyticsResolution(ctx.Request.Query.ContainsKey("as")
            ? new AnalyticsViewer("amy", false, true)
            : new AnalyticsViewer("owner", true, false))));
        var client = app.GetTestClient();

        var owner = await Agents(client, "/analytics");
        var amy = await Agents(client, "/analytics?as=1");

        Assert.Equal(["amy", "bob"], owner);
        Assert.Equal(["amy"], amy);
    }

    private static async Task<string[]> Agents(HttpClient client, string url)
    {
        var json = await client.GetStringAsync(url, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("bazaar.agent-analytics", doc.RootElement.GetProperty("kind").GetString());
        return doc.RootElement.GetProperty("model").GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("agentId").GetString()!).ToArray();
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Source : IWorkEventSource
    {
        public Task<IReadOnlyList<AgentWorkEvent>> ReadAsync(AnalyticsWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentWorkEvent>>([
                new("amy", AgentWorkEventKind.TaskCompleted, _now.AddDays(-1)),
                new("bob", AgentWorkEventKind.PrMerged, _now.AddDays(-2)),
            ]);
    }
}
