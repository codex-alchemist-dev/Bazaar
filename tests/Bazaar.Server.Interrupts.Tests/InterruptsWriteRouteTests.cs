using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Interrupts.Tests;

/// <summary>The owner producer routes (doc 24 section 3, "Owner producer") on a real loopback Kestrel, over an in-memory stack fake.</summary>
public sealed class InterruptsWriteRouteTests : IAsyncLifetime
{
    private static readonly Guid _owner = Guid.Parse("00000000-0000-7000-8000-0000000000f0");
    private static readonly Guid _agent = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _gc = Guid.Parse("00000000-0000-7000-8000-0000000000c3");

    private readonly FakeStack _stack = new();
    private InterruptViewer _viewer = new(_owner, true);
    private IResult? _refusal;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IInterruptStackService>(_stack);
        builder.Services.AddScoped(_ => _viewer);
        builder.Services.AddSingleton<TimeProvider>(new FixedClock());
        _app = builder.Build();
        _app.MapCompanyInterruptsWrite(_ => ValueTask.FromResult(_refusal));
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string body) =>
        await _http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static string PushBody(string reason = "look at the build", string extra = "") =>
        $$"""{"agentId":"{{_agent}}","gcId":"{{_gc}}","reason":"{{reason}}"{{extra}}}""";

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Owner_push_answers_200_and_authors_the_frame_as_the_owner()
    {
        var response = await PostAsync(InterruptsWriteRoute.PushPath, PushBody(extra: ",\"ttlSeconds\":60"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await JsonAsync(response);
        Assert.Equal("owner", doc.RootElement.GetProperty("frame").GetProperty("source").GetString());
        Assert.Equal("active", doc.RootElement.GetProperty("frame").GetProperty("state").GetString());
        Assert.False(doc.RootElement.GetProperty("wakeBypassesVacation").GetBoolean());
        var call = Assert.Single(_stack.Pushes);
        Assert.Equal(_owner, call.Author);
        Assert.Equal(InterruptSource.Owner, call.Source);
        Assert.Equal(_agent, call.AgentId);
        Assert.Equal(_gc, call.GcId);
        Assert.Equal(FixedClock.Now.AddSeconds(60), call.TtlAt);
    }

    [Theory]
    [InlineData(",\"emergency\":true", true)]
    [InlineData(",\"emergency\":false", false)]
    [InlineData("", false)]
    public async Task Emergency_tag_sets_the_vacation_bypass(string extra, bool expected)
    {
        var response = await PostAsync(InterruptsWriteRoute.PushPath, PushBody(extra: extra));

        using var doc = await JsonAsync(response);
        Assert.Equal(expected, doc.RootElement.GetProperty("wakeBypassesVacation").GetBoolean());
        Assert.Equal(expected, doc.RootElement.GetProperty("frame").GetProperty("emergency").GetBoolean());
        Assert.Equal(expected, Assert.Single(_stack.Pushes).Emergency);
        Assert.Equal(expected, Assert.Single(_stack.Live).Emergency);
    }

    [Fact]
    public async Task Third_push_answers_409_depth_exceeded_and_changes_nothing()
    {
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(InterruptsWriteRoute.PushPath, PushBody("one"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(InterruptsWriteRoute.PushPath, PushBody("two"))).StatusCode);

        var third = await PostAsync(InterruptsWriteRoute.PushPath, PushBody("three"));

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        using var doc = await JsonAsync(third);
        Assert.Equal("depth_exceeded", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(2, _stack.Live.Count);
    }

    [Fact]
    public async Task Pop_is_idempotent_by_frame_id()
    {
        using var pushed = await JsonAsync(await PostAsync(InterruptsWriteRoute.PushPath, PushBody()));
        var frameId = pushed.RootElement.GetProperty("frame").GetProperty("id").GetString();
        var body = $$"""{"agentId":"{{_agent}}","frameId":"{{frameId}}"}""";

        var first = await PostAsync(InterruptsWriteRoute.PopPath, body);
        var second = await PostAsync(InterruptsWriteRoute.PopPath, body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var doc = await JsonAsync(second);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("resumed").ValueKind);
        Assert.Empty(_stack.Live);
    }

    [Fact]
    public async Task Pop_returns_the_resumed_frame()
    {
        await PostAsync(InterruptsWriteRoute.PushPath, PushBody("below"));
        using var top = await JsonAsync(await PostAsync(InterruptsWriteRoute.PushPath, PushBody("above")));
        var topId = top.RootElement.GetProperty("frame").GetProperty("id").GetString();

        var response = await PostAsync(InterruptsWriteRoute.PopPath, $$"""{"agentId":"{{_agent}}","frameId":"{{topId}}"}""");

        using var doc = await JsonAsync(response);
        Assert.Equal("active", doc.RootElement.GetProperty("resumed").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Popping_a_suspended_frame_answers_409_not_top()
    {
        using var bottom = await JsonAsync(await PostAsync(InterruptsWriteRoute.PushPath, PushBody("below")));
        await PostAsync(InterruptsWriteRoute.PushPath, PushBody("above"));
        var bottomId = bottom.RootElement.GetProperty("frame").GetProperty("id").GetString();

        var response = await PostAsync(InterruptsWriteRoute.PopPath, $$"""{"agentId":"{{_agent}}","frameId":"{{bottomId}}"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = await JsonAsync(response);
        Assert.Equal("not_top", doc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"agentId":"x","gcId":"00000000-0000-7000-8000-0000000000c3","reason":"r"}""")]
    [InlineData("""{"agentId":"00000000-0000-7000-8000-0000000000a1","gcId":"00000000-0000-7000-8000-0000000000c3"}""")]
    [InlineData("""{"agentId":"00000000-0000-7000-8000-0000000000a1","gcId":"00000000-0000-7000-8000-0000000000c3","reason":"  "}""")]
    [InlineData("""{"agentId":"00000000-0000-7000-8000-0000000000a1","gcId":"00000000-0000-7000-8000-0000000000c3","reason":"r","emergency":"yes"}""")]
    [InlineData("""{"agentId":"00000000-0000-7000-8000-0000000000a1","gcId":"00000000-0000-7000-8000-0000000000c3","reason":"r","ttlSeconds":0}""")]
    [InlineData("""{"agentId":"00000000-0000-7000-8000-0000000000a1","gcId":"00000000-0000-7000-8000-0000000000c3","reason":"r","ttlSeconds":999999999}""")]
    public async Task Bad_push_bodies_answer_400(string body)
    {
        var response = await PostAsync(InterruptsWriteRoute.PushPath, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_stack.Pushes);
    }

    [Fact]
    public async Task A_reason_over_the_limit_and_an_oversized_body_answer_400()
    {
        var tooLong = await PostAsync(InterruptsWriteRoute.PushPath, PushBody(new string('x', InterruptStackService.MaxReasonLength + 1)));
        var huge = await PostAsync(InterruptsWriteRoute.PushPath, PushBody(new string('x', 5000)));

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"agentId":"00000000-0000-7000-8000-0000000000a1","frameId":"nope"}""")]
    public async Task Bad_pop_bodies_answer_400(string body) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(InterruptsWriteRoute.PopPath, body)).StatusCode);

    [Theory]
    [InlineData(InterruptsWriteRoute.PushPath)]
    [InlineData(InterruptsWriteRoute.PopPath)]
    public async Task Agent_and_anonymous_viewers_are_refused_with_403_and_write_nothing(string path)
    {
        foreach (var viewer in new[] { new InterruptViewer(_agent, false), InterruptViewer.Anonymous, new InterruptViewer(Guid.Empty, true) })
        {
            _viewer = viewer;
            var response = await PostAsync(path, PushBody());
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Empty(_stack.Pushes);
        Assert.Empty(_stack.Pops);
    }

    [Fact]
    public async Task A_guard_refusal_is_returned_as_it_is()
    {
        _refusal = Results.Text("{}", "application/json", statusCode: StatusCodes.Status401Unauthorized);

        var response = await PostAsync(InterruptsWriteRoute.PushPath, PushBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_stack.Pushes);
    }

    [Fact]
    public async Task Get_is_not_routed()
    {
        var response = await _http.GetAsync(InterruptsWriteRoute.PushPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private sealed class FixedClock : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record PushCall(Guid AgentId, Guid Author, InterruptSource Source, Guid GcId, DateTimeOffset? TtlAt, bool Emergency);

    /// <summary>An in-memory stack with the real service's depth and pop rules.</summary>
    private sealed class FakeStack : IInterruptStackService
    {
        private readonly List<InterruptFrame> _frames = [];

        public List<PushCall> Pushes { get; } = [];

        public List<Guid> Pops { get; } = [];

        public List<InterruptFrame> Live => [.. _frames.Where(f => f.State is InterruptFrameState.Active or InterruptFrameState.Suspended)];

        public Task<InterruptFrame> PushAsync(
            Guid agentId, Guid authorId, InterruptSource source, string reason, Guid gcId, DateTimeOffset? ttlAt = null, bool emergency = false,
            CancellationToken ct = default)
        {
            if (Live.Count(f => f.AgentId == agentId) >= 2)
            {
                throw new InterruptDepthExceededException(agentId);
            }

            Pushes.Add(new PushCall(agentId, authorId, source, gcId, ttlAt, emergency));
            for (var i = 0; i < _frames.Count; i++)
            {
                if (_frames[i].AgentId == agentId && _frames[i].State == InterruptFrameState.Active)
                {
                    _frames[i] = _frames[i] with { State = InterruptFrameState.Suspended };
                }
            }

            var frame = new InterruptFrame(Guid.NewGuid(), agentId, null, source, reason, gcId, InterruptFrameState.Active, FixedClock.Now, ttlAt, 1, emergency);
            _frames.Add(frame);
            return Task.FromResult(frame);
        }

        public Task<InterruptFrame?> PopAsync(Guid agentId, Guid frameId, CancellationToken ct = default)
        {
            Pops.Add(frameId);
            var index = _frames.FindIndex(f => f.Id == frameId);
            if (index < 0 || _frames[index].State is InterruptFrameState.Done or InterruptFrameState.Cancelled)
            {
                return Task.FromResult<InterruptFrame?>(null);
            }

            if (_frames[index].State == InterruptFrameState.Suspended)
            {
                throw new InterruptFrameNotTopException(frameId);
            }

            _frames[index] = _frames[index] with { State = InterruptFrameState.Done };
            var below = _frames.FindLastIndex(f => f.AgentId == agentId && f.State == InterruptFrameState.Suspended);
            if (below < 0)
            {
                return Task.FromResult<InterruptFrame?>(null);
            }

            _frames[below] = _frames[below] with { State = InterruptFrameState.Active };
            return Task.FromResult<InterruptFrame?>(_frames[below]);
        }

        public Task<IReadOnlyList<InterruptFrame>> GetStackAsync(Guid agentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InterruptFrame>>([.. Live.Where(f => f.AgentId == agentId)]);
    }
}
