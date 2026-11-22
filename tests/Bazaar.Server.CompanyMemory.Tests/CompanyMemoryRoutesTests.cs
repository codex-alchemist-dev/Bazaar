using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.CompanyMemory.Tests;

/// <summary>Routes on a real listener bound to loopback only.</summary>
public sealed class CompanyMemoryRoutesTests
{
    private static ValueTask<IResult?> Allow(HttpContext _) => ValueTask.FromResult<IResult?>(null);

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Func<HttpContext, ValueTask<IResult?>> guard)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ICompanyDecisionStore, InMemoryCompanyDecisionStore>();
        builder.Services.AddSingleton<ICompanyMemory>(sp => new CompanyMemoryService(sp.GetRequiredService<ICompanyDecisionStore>()));
        var app = builder.Build();
        app.MapCompanyMemory(guard);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static StringContent Json(string slug, string? supersedes = null) =>
        new(
            $$"""{"docId":"{{Guid.NewGuid()}}","slug":"{{slug}}","title":"T {{slug}}","body":"Body.","agentId":"agent-ana","supersedes":{{(supersedes is null ? "null" : $"[\"{supersedes}\"]")}}}""",
            Encoding.UTF8,
            "application/json");

    [Fact]
    public async Task One_call_records_and_another_reads_the_chain()
    {
        var (app, client) = await StartAsync(Allow);
        await using (app)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/company-memory/decisions", Json("a"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/company-memory/decisions", Json("b", "0001"), ct)).StatusCode);

            var response = await client.GetAsync("/company-memory/decisions/0001/chain", ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("decisions/0002-b.md", body.RootElement.GetProperty("newer")[0].GetString());
        }
    }

    [Fact]
    public async Task Unknown_decision_is_404_and_bad_supersedes_is_400()
    {
        var (app, client) = await StartAsync(Allow);
        await using (app)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/company-memory/decisions/0009/chain", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/company-memory/decisions", Json("a", "0009"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/company-memory/decisions", new StringContent("{", Encoding.UTF8, "application/json"), ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_refusing_guard_blocks_both_routes()
    {
        var (app, client) = await StartAsync(_ => ValueTask.FromResult<IResult?>(Results.StatusCode(401)));
        await using (app)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/company-memory/decisions", Json("a"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/company-memory/decisions/0001/chain", ct)).StatusCode);
        }
    }
}
