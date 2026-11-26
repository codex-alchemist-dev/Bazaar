using System.Net;
using System.Net.Http.Json;
using Bazaar.Client.GeneticMemory;
using Bazaar.Server.GeneticMemory;
using Bazaar.Server.GeneticMemory.Abstractions;
using Bazaar.Server.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Tests.GeneticMemory;

/// <summary>The review routes over real HTTP, driven by the client's <see cref="HttpGmtQueueClient"/>.</summary>
public sealed class GmtRoutesTests : IAsyncLifetime
{
    private sealed class HeaderResolver : IGmtReviewerResolver
    {
        public Task<GmtReviewer?> ResolveAsync(HttpContext http, CancellationToken ct) =>
            Task.FromResult<GmtReviewer?>(http.Request.Headers["X-Role"].ToString() switch
            {
                "owner" => GmtReviewer.Owner,
                "architect" => GmtReviewer.Architect,
                _ => null,
            });
    }

    private sealed class NullSink : Bazaar.Server.Memory.Write.IMemoryBatchSink
    {
        public Dictionary<string, string> Files { get; } = [];

        public Task<Bazaar.Server.Memory.Write.MemoryCommitResult> CommitAsync(Bazaar.Server.Memory.Write.MemoryBatch batch, CancellationToken ct)
        {
            foreach (var item in batch.Items)
            {
                Files[item.Path] = item.Content;
            }

            return Task.FromResult(Bazaar.Server.Memory.Write.MemoryCommitResult.Success("sha"));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly NullSink _sink = new();
    private WebApplication _app = null!;
    private GeneticMemoryService _service = null!;
    private int _guardCalls;

    public async ValueTask InitializeAsync() => await StartAsync(withResolver: true);

    private async Task StartAsync(bool withResolver)
    {
        _service = new GeneticMemoryService(new InMemoryGmtLedger(), new GmtLessonWriter(_sink), new GmtSecretScanner());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IGeneticMemoryTransfer>(_service);
        if (withResolver)
        {
            builder.Services.AddSingleton<IGmtReviewerResolver, HeaderResolver>();
        }

        _app = builder.Build();
        _app.MapBazaarGeneticMemory(route =>
        {
            _guardCalls++;
            return route;
        });
        await _app.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private HttpClient Http(string? role)
    {
        var http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        if (role is not null)
        {
            http.DefaultRequestHeaders.Add("X-Role", role);
        }

        return http;
    }

    private Task<GmtOutcome> Stage(string title, GmtContentClass contentClass = GmtContentClass.Lesson, string body = "Back off on retry.") =>
        _service.StageAsync(new GmtStageRequest("child-1", "agent-new", contentClass, GmtLessonKind.Procedure, title, body, "trace/abc", true, false, GmtTerms.None), Ct);

    [Fact]
    public async Task All_three_routes_are_wrapped_by_the_guard()
    {
        await Task.CompletedTask;
        Assert.Equal(3, _guardCalls);
    }

    [Fact]
    public async Task The_client_lists_the_queue_with_every_field()
    {
        await Stage("Retry");
        using var http = Http("owner");

        var items = await new HttpGmtQueueClient(http).ListAsync(GmtReviewerRole.Owner, Ct);

        var item = Assert.Single(items);
        Assert.Equal("Retry", item.Title);
        Assert.Equal("child-1", item.ChildAgent);
        Assert.Equal("agent-new", item.TargetAgent);
        Assert.Equal("Lesson", item.ContentClass);
        Assert.Equal("trace/abc", item.TraceRef);
        Assert.True(item.Tainted);
        Assert.False(item.OwnerOnly);
    }

    [Fact]
    public async Task The_architect_sees_only_what_the_setting_routes_to_the_architect()
    {
        await Stage("Retry");
        await Stage("Grant shell", GmtContentClass.ToolGrant);

        using var architect = Http("architect");
        using var owner = Http("owner");

        Assert.Equal(["Retry"], (await new HttpGmtQueueClient(architect).ListAsync(GmtReviewerRole.Architect, Ct)).Select(i => i.Title));
        Assert.Equal(2, (await new HttpGmtQueueClient(owner).ListAsync(GmtReviewerRole.Owner, Ct)).Count);
    }

    [Fact]
    public async Task A_client_cannot_claim_a_role_it_does_not_have()
    {
        await Stage("Grant shell", GmtContentClass.ToolGrant);
        using var http = Http("architect");

        // The client passes Owner, but the server resolves the caller as architect, so the owner-only item is not listed.
        var items = await new HttpGmtQueueClient(http).ListAsync(GmtReviewerRole.Owner, Ct);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Accepting_through_the_client_writes_the_note_with_the_edit()
    {
        var staged = await Stage("Retry");
        using var http = Http("owner");

        var result = await new HttpGmtQueueClient(http).AcceptAsync(staged.Id!.Value, GmtReviewerRole.Owner, null, "Cap the delay at 30 seconds.", Ct);

        Assert.True(result.Succeeded);
        var file = Assert.Single(_sink.Files);
        Assert.Equal("agents/agent-new/memory/lessons/retry.md", file.Key);
        Assert.Contains("Cap the delay", file.Value, StringComparison.Ordinal);
        Assert.Empty(await _service.QueueAsync(GmtReviewer.Owner, Ct));
    }

    [Fact]
    public async Task Rejecting_through_the_client_removes_it_and_writes_nothing()
    {
        var staged = await Stage("Retry");
        using var http = Http("architect");

        var result = await new HttpGmtQueueClient(http).RejectAsync(staged.Id!.Value, GmtReviewerRole.Architect, Ct);

        Assert.True(result.Succeeded);
        Assert.Empty(_sink.Files);
        Assert.Empty(await _service.QueueAsync(GmtReviewer.Owner, Ct));
    }

    [Fact]
    public async Task A_secret_in_an_edit_is_a_422_with_a_code_and_the_candidate_stays()
    {
        var staged = await Stage("Retry");
        using var http = Http("owner");

        var response = await http.PostAsJsonAsync($"gmt/queue/{staged.Id}/accept", new { body = "key ghp_abcdefghijklmnopqrstuvwxyz0123456789" }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var viaClient = await new HttpGmtQueueClient(http).AcceptAsync(staged.Id!.Value, GmtReviewerRole.Owner, null, "ghp_abcdefghijklmnopqrstuvwxyz0123456789", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("{\"error\":\"secret_found\"}", text);
        Assert.False(viaClient.Succeeded);
        Assert.Contains("key or token", viaClient.Message, StringComparison.Ordinal);
        Assert.Single(await _service.QueueAsync(GmtReviewer.Owner, Ct));
    }

    [Fact]
    public async Task The_architect_cannot_decide_an_owner_only_candidate()
    {
        var staged = await Stage("Grant shell", GmtContentClass.ToolGrant);
        using var http = Http("architect");

        var response = await http.PostAsync($"gmt/queue/{staged.Id}/reject", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await _service.QueueAsync(GmtReviewer.Owner, Ct));
    }

    [Fact]
    public async Task Unknown_decided_and_malformed_ids_answer_404_409_and_400()
    {
        var staged = await Stage("Retry");
        using var http = Http("owner");
        await http.PostAsync($"gmt/queue/{staged.Id}/reject", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"gmt/queue/{Guid.NewGuid()}/reject", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"gmt/queue/{staged.Id}/reject", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("gmt/queue/not-a-guid/accept", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_caller_with_no_review_role_gets_403_everywhere_and_the_client_lists_nothing()
    {
        var staged = await Stage("Retry");
        using var http = Http(null);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("gmt/queue", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync($"gmt/queue/{staged.Id}/accept", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync($"gmt/queue/{staged.Id}/reject", null, Ct)).StatusCode);
        Assert.Empty(await new HttpGmtQueueClient(http).ListAsync(GmtReviewerRole.Owner, Ct));
        Assert.Single(await _service.QueueAsync(GmtReviewer.Owner, Ct));
    }

    [Fact]
    public async Task Without_a_reviewer_resolver_every_route_fails_closed()
    {
        await _app.DisposeAsync();
        await StartAsync(withResolver: false);
        await Stage("Retry");
        using var http = Http("owner");

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("gmt/queue", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_malformed_body_is_a_400()
    {
        var staged = await Stage("Retry");
        using var http = Http("owner");

        var response = await http.PostAsync($"gmt/queue/{staged.Id}/accept", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
