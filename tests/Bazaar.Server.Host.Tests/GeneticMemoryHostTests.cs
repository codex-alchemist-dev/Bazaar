using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.GeneticMemory;
using Bazaar.Server.GeneticMemory.Abstractions;
using Bazaar.Server.Host.GeneticMemory;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Http;
using Bazaar.Server.Identity;
using Bazaar.Server.Memory;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public sealed class GeneticMemoryHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PrincipalRef _owner = new(Guid.NewGuid(), "owner");
    private static readonly PrincipalRef _agent = new(Guid.NewGuid(), "agent");

    private sealed class TokenAuthenticator : IConnectionAuthenticator
    {
        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken ct) =>
            ValueTask.FromResult(credential?.Value switch
            {
                "owner-token" => AuthResult.Success(_owner),
                "agent-token" => AuthResult.Success(_agent),
                _ => AuthResult.Fail("bad token"),
            });
    }

    private sealed class NullSink : Bazaar.Server.Memory.Write.IMemoryBatchSink
    {
        public Task<Bazaar.Server.Memory.Write.MemoryCommitResult> CommitAsync(Bazaar.Server.Memory.Write.MemoryBatch batch, CancellationToken ct) =>
            Task.FromResult(Bazaar.Server.Memory.Write.MemoryCommitResult.Success("sha"));
    }

    private static async Task<(WebApplication App, HttpClient Client, GeneticMemoryService Service)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var service = new GeneticMemoryService(new InMemoryGmtLedger(), new GmtLessonWriter(new NullSink()), new GmtSecretScanner());
        builder.Services.AddSingleton<IGeneticMemoryTransfer>(service);
        builder.Services.AddBazaarGeneticMemoryHost();
        builder.Services.AddSingleton<IConnectionAuthenticator, TokenAuthenticator>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(Options.Create(new WireOptions()));
        builder.Services.AddSingleton(new HttpRateLimiter(new HttpSurfaceOptions(), TimeProvider.System));
        var app = builder.Build();
        app.UseBazaarGetOnly();
        app.MapBazaarGeneticMemoryHost();
        await app.StartAsync(Ct);
        return (app, app.GetTestClient(), service);
    }

    private static void Bearer(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static Task<GmtOutcome> Stage(GeneticMemoryService service) =>
        service.StageAsync(new GmtStageRequest("child-1", "agent-new", GmtContentClass.Lesson, GmtLessonKind.Procedure, "Retry", "Back off.", null, false, false, GmtTerms.None), Ct);

    [Fact]
    public async Task The_owner_lists_and_accepts_through_the_host_routes_with_post_allowed_past_the_method_gate()
    {
        var (app, client, service) = await StartAsync();
        await using (app)
        {
            var staged = await Stage(service);
            Bearer(client, "owner-token");

            var list = await client.GetAsync("/gmt/queue", Ct);
            var accept = await client.PostAsync($"/gmt/queue/{staged.Id}/accept", new StringContent(string.Empty), Ct);

            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Contains("Retry", await list.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
            Assert.Empty(await service.QueueAsync(GmtReviewer.Owner, Ct));
        }
    }

    [Fact]
    public async Task An_agent_is_refused_and_no_token_or_a_bad_token_is_unauthorized()
    {
        var (app, client, service) = await StartAsync();
        await using (app)
        {
            var staged = await Stage(service);

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/gmt/queue", Ct)).StatusCode);
            Bearer(client, "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/gmt/queue", Ct)).StatusCode);
            Bearer(client, "agent-token");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/gmt/queue", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/gmt/queue/{staged.Id}/accept", new StringContent(string.Empty), Ct)).StatusCode);
            Assert.Single(await service.QueueAsync(GmtReviewer.Owner, Ct));
        }
    }

    [Fact]
    public async Task Other_methods_on_the_review_routes_stay_refused_by_the_method_gate()
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            Bearer(client, "owner-token");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync("/gmt/queue", Ct)).StatusCode);
        }
    }

    private sealed class FakeTaint(TaintState state) : ISessionTaintStore
    {
        public Task<SessionTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(new SessionTaint(state, ImmutableArray<TaintSource>.Empty));

        public Task SaveAsync(Guid sessionId, SessionTaint taint, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeState(bool quarantined) : IAgentStateReader
    {
        public SessionTaint TaintFor(string agentId) => SessionTaint.Clean;

        public bool IsQuarantined(string agentId) => quarantined;
    }

    private static SessionChildContext Context(ISessionTaintStore? taint, IAgentStateReader? state = null)
    {
        var services = new ServiceCollection();
        if (taint is not null)
        {
            services.AddSingleton(taint);
        }

        if (state is not null)
        {
            services.AddSingleton(state);
        }

        return new SessionChildContext(services.BuildServiceProvider());
    }

    [Theory]
    [InlineData(TaintState.Clean, false)]
    [InlineData(TaintState.Soft, true)]
    [InlineData(TaintState.Hard, true)]
    public async Task The_session_lookup_uses_the_agent_id_as_the_slug_and_the_session_taint(TaintState state, bool tainted)
    {
        var agent = Guid.NewGuid();

        var context = await Context(new FakeTaint(state)).ResolveAsync(agent, Guid.NewGuid(), Ct);

        Assert.NotNull(context);
        Assert.Equal(agent.ToString("D"), context.TargetAgent);
        Assert.Equal(tainted, context.Tainted);
        Assert.False(context.Quarantined);
    }

    [Fact]
    public async Task The_session_lookup_reports_quarantine_and_fails_closed_without_a_taint_store_or_ids()
    {
        Assert.True((await Context(new FakeTaint(TaintState.Clean), new FakeState(true)).ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), Ct))!.Quarantined);
        Assert.Null(await Context(null).ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), Ct));
        Assert.Null(await Context(new FakeTaint(TaintState.Clean)).ResolveAsync(Guid.Empty, Guid.NewGuid(), Ct));
        Assert.Null(await Context(new FakeTaint(TaintState.Clean)).ResolveAsync(Guid.NewGuid(), Guid.Empty, Ct));
    }

    [Fact]
    public async Task The_agent_slug_is_a_valid_memory_slug()
    {
        var context = await Context(new FakeTaint(TaintState.Clean)).ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), Ct);

        Assert.True(Bazaar.Server.Memory.Notes.MemoryAreas.CheckPath(
            $"agents/{context!.TargetAgent}/memory/lessons/x.md", Bazaar.Server.Memory.Notes.MemoryKind.Procedural).IsValid);
    }
}
