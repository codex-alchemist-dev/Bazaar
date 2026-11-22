using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Agents.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Interrupts.Tests;

/// <summary>
/// <c>POST /company/interrupts</c> on a real loopback Kestrel. The client's reader lives in a client module this server module
/// may not reference, so the shape is asserted field by field against what <c>InterruptStackModelReader</c> parses (doc 24 section 5).
/// </summary>
public sealed class InterruptsReadRouteTests : IAsyncLifetime
{
    private static readonly Guid _alice = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-0000000000b2");
    private static readonly Guid _gc = Guid.Parse("00000000-0000-7000-8000-0000000000c3");

    private readonly FakeReader _reader = new();
    private IResult? _refusal;
    private int _guardCalls;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _app = await StartAsync(_reader, new FakeDirectory());
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<WebApplication> StartAsync(IInterruptStackReader? reader, IAgentDirectoryReader? directory)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (reader is not null)
        {
            builder.Services.AddSingleton(reader);
        }

        if (directory is not null)
        {
            builder.Services.AddSingleton(directory);
        }

        var app = builder.Build();
        app.MapCompanyInterrupts(_ =>
        {
            _guardCalls++;
            return ValueTask.FromResult(_refusal);
        });
        await app.StartAsync();
        return app;
    }

    private async Task<HttpResponseMessage> Post(string body) =>
        await _http.PostAsync("/company/interrupts", new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static InterruptFrame Frame(
        Guid agent, InterruptSource source, string reason, InterruptFrameState state, DateTimeOffset pushedAt) =>
        new(Guid.NewGuid(), agent, null, source, reason, _gc, state, pushedAt, null, 1);

    [Fact]
    public async Task Answers_the_pane_model_the_client_reader_parses()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        var bobTop = Frame(_bob, InterruptSource.SystemBuild, "main is red", InterruptFrameState.Active, t0.AddMinutes(5));
        var bobUnder = Frame(_bob, InterruptSource.Owner, "look at the demo", InterruptFrameState.Suspended, t0);
        var alice = Frame(_alice, InterruptSource.SystemSecurity, "leaked key", InterruptFrameState.Active, t0);
        _reader.Rows = [bobTop, bobUnder, alice];

        using var response = await Post("{}");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var model = JsonElement.Parse(text);
        var agents = model.GetProperty("agents").EnumerateArray().ToArray();
        Assert.Equal(["Alice", "Bob"], agents.Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(_alice.ToString(), agents[0].GetProperty("agentId").GetString());
        Assert.Equal(_bob.ToString(), agents[1].GetProperty("agentId").GetString());

        var aliceFrame = Assert.Single(agents[0].GetProperty("frames").EnumerateArray());
        Assert.Equal(alice.Id.ToString(), aliceFrame.GetProperty("id").GetString());
        Assert.Equal("system_security", aliceFrame.GetProperty("source").GetString());
        Assert.Equal("leaked key", aliceFrame.GetProperty("reason").GetString());
        Assert.Equal("active", aliceFrame.GetProperty("state").GetString());
        Assert.Equal(t0, ParseUtc(aliceFrame.GetProperty("pushedAt").GetString()!));

        // Top frame first, then the suspended one; wire names match what the client reader switches on.
        var bobFrames = agents[1].GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(2, bobFrames.Length);
        Assert.Equal(bobTop.Id.ToString(), bobFrames[0].GetProperty("id").GetString());
        Assert.Equal("system_build", bobFrames[0].GetProperty("source").GetString());
        Assert.Equal("active", bobFrames[0].GetProperty("state").GetString());
        Assert.Equal(bobUnder.Id.ToString(), bobFrames[1].GetProperty("id").GetString());
        Assert.Equal("owner", bobFrames[1].GetProperty("source").GetString());
        Assert.Equal("suspended", bobFrames[1].GetProperty("state").GetString());
        Assert.Equal("look at the demo", bobFrames[1].GetProperty("reason").GetString());
        Assert.Equal(t0, ParseUtc(bobFrames[1].GetProperty("pushedAt").GetString()!));
    }

    [Fact]
    public async Task No_live_stacks_answers_an_empty_agent_list()
    {
        using var response = await Post("{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = JsonElement.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(model.GetProperty("agents").EnumerateArray());
    }

    [Fact]
    public async Task Agent_the_directory_does_not_list_shows_its_id_as_the_name()
    {
        var stranger = Guid.Parse("00000000-0000-7000-8000-0000000000d4");
        _reader.Rows = [Frame(stranger, InterruptSource.Owner, "x", InterruptFrameState.Active, DateTimeOffset.UnixEpoch)];

        using var response = await Post("{}");

        var model = JsonElement.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var agent = Assert.Single(model.GetProperty("agents").EnumerateArray());
        Assert.Equal(stranger.ToString(), agent.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Without_a_registered_reader_the_model_lists_no_agents()
    {
        await using var app = await StartAsync(reader: null, directory: null);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var response = await http.PostAsync(
            "/company/interrupts", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = JsonElement.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(model.GetProperty("agents").EnumerateArray());
    }

    [Fact]
    public async Task Empty_body_counts_as_empty_object()
    {
        using var response = await Post(string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("\"x\"")]
    public async Task Body_that_is_not_an_object_is_400(string body)
    {
        using var response = await Post(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _reader.Calls);
    }

    [Fact]
    public async Task Oversized_body_is_400()
    {
        using var response = await Post("{\"pad\":\"" + new string('x', 5000) + "\"}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _reader.Calls);
    }

    [Fact]
    public async Task Guard_refusal_is_returned_and_nothing_is_read()
    {
        _refusal = Results.StatusCode(StatusCodes.Status401Unauthorized);

        using var response = await Post("{}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, _guardCalls);
        Assert.Equal(0, _reader.Calls);
    }

    [Fact]
    public async Task Get_has_no_route()
    {
        using var response = await _http.GetAsync("/company/interrupts", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public void WriteModel_leaves_out_ended_frames_and_agents_that_have_only_those()
    {
        var done = Frame(_alice, InterruptSource.Owner, "old", InterruptFrameState.Done, DateTimeOffset.UnixEpoch);

        var model = JsonElement.Parse(InterruptsReadRoute.WriteModel([done], new Dictionary<Guid, string>()));

        Assert.Empty(model.GetProperty("agents").EnumerateArray());
    }

    private sealed class FakeReader : IInterruptStackReader
    {
        public IReadOnlyList<InterruptFrame> Rows { get; set; } = [];

        public int Calls { get; private set; }

        public Task<IReadOnlyList<InterruptFrame>> ListLiveFramesAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Rows);
        }
    }

    private sealed class FakeDirectory : IAgentDirectoryReader
    {
        public Task<IReadOnlyList<AgentListing>> ListActiveAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentListing>>([new AgentListing(_alice, "Alice"), new AgentListing(_bob, "Bob")]);
    }
}
