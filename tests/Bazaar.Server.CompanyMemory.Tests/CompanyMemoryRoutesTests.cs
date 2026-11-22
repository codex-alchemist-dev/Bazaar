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
    private static Task<IResult> Allow(HttpContext http, Func<HttpContext, string, Task<IResult>> run) => run(http, "caller-1");

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(CompanyMemoryGuard guard)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ICompanyDecisionStore>(sp => sp.GetRequiredService<RecordingStore>());
        builder.Services.AddSingleton<ICompanyMemory>(sp => new CompanyMemoryService(sp.GetRequiredService<ICompanyDecisionStore>(), pages: new InMemoryKnowledgePageStore()));
        builder.Services.AddSingleton<RecordingStore>();
        var app = builder.Build();
        app.MapCompanyMemory(guard);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static StringContent Json(string slug, string? supersedes = null) =>
        new(
            $$"""{"docId":"{{Guid.NewGuid()}}","slug":"{{slug}}","title":"T {{slug}}","body":"Body.","supersedes":{{(supersedes is null ? "null" : $"[\"{supersedes}\"]")}}}""",
            Encoding.UTF8,
            "application/json");

    private sealed class RecordingStore : ICompanyDecisionStore
    {
        private readonly InMemoryCompanyDecisionStore _inner = new();

        public List<string> Agents { get; } = [];

        public Task<IReadOnlyList<Bazaar.Server.Knowledge.CompanyMemory.DecisionRecord>> ListAsync(CancellationToken ct) => _inner.ListAsync(ct);

        public Task<RecordedDecision> CreateAsync(RecordDecisionRequest request, CancellationToken ct)
        {
            Agents.Add(request.AgentId);
            return _inner.CreateAsync(request, ct);
        }
    }

    [Fact]
    public async Task The_recording_agent_is_the_authenticated_caller_even_if_the_body_names_another()
    {
        var (app, client) = await StartAsync(Allow);
        await using (app)
        {
            var body = new StringContent(
                $$"""{"docId":"{{Guid.NewGuid()}}","slug":"a","title":"T","body":"Body.","agentId":"someone-else"}""",
                Encoding.UTF8,
                "application/json");

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/company-memory/decisions", body, TestContext.Current.CancellationToken)).StatusCode);

            Assert.Equal(["caller-1"], app.Services.GetRequiredService<RecordingStore>().Agents);
        }
    }

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
        var (app, client) = await StartAsync((_, _) => Task.FromResult(Results.StatusCode(401)));
        await using (app)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/company-memory/decisions", Json("a"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/company-memory/decisions/0001/chain", ct)).StatusCode);
        }
    }

    private static StringContent Page(Guid id, string slug, string body) =>
        new($$"""{"docId":"{{id}}","slug":"{{slug}}","title":"T","body":"{{body}}","agentId":"someone-else"}""", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Pages_are_written_listed_and_read_over_http()
    {
        var (app, client) = await StartAsync(Allow);
        await using (app)
        {
            var ct = TestContext.Current.CancellationToken;
            var id = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/company-memory/pages", Page(id, "onboarding", "Welcome."), ct)).StatusCode);

            var read = await client.GetAsync("/company-memory/pages/onboarding", ct);
            using var one = JsonDocument.Parse(await read.Content.ReadAsStringAsync(ct));
            var list = await client.GetAsync("/company-memory/pages", ct);
            using var all = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct));

            Assert.Equal("Welcome.", one.RootElement.GetProperty("body").GetString());
            Assert.Equal("onboarding", all.RootElement[0].GetProperty("slug").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/company-memory/pages/missing", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/company-memory/pages", Page(Guid.NewGuid(), "Bad Slug", "x"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/company-memory/pages", Page(Guid.NewGuid(), "onboarding", "Other."), ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_refusing_guard_blocks_the_page_routes()
    {
        var (app, client) = await StartAsync((_, _) => Task.FromResult(Results.StatusCode(401)));
        await using (app)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/company-memory/pages", Page(Guid.NewGuid(), "a", "x"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/company-memory/pages", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/company-memory/pages/a", ct)).StatusCode);
        }
    }
}
