using System.Net;
using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Secrets;
using Bazaar.Server.Tests.Gateway.Adapters.OpenAi;
using Bazaar.Server.Tests.Gateway.Pipeline;
using Bazaar.Server.Tests.Gateway.Policy;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

/// <summary>
/// FreeLLMAPI end to end through the real gateway pipeline: the config overlay, the key served for its reference, the
/// openai_chat decorator that reads routing headers, and the Retry-After quota. Upstream is an <see cref="HttpMessageHandler"/>
/// stub, so nothing touches the network. Every key is a placeholder.
/// </summary>
public sealed class FreeLlmApiGatewayTests
{
    private const string _agent = "responder";
    private const string _model = "llama-3.3-70b";
    private const string _keyEnv = "FREELLMAPI_TEST_KEY";
    private const string _testKeyValue = "freellmapi-TEST-KEY-NOT-A-SECRET";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One gateway wired the way the host wires it, with the FreeLLMAPI key read from <see cref="_keyEnv"/>.</summary>
    private sealed class Rig
    {
        public Rig(ISecretResolver? resolver = null)
        {
            Clock = new FakeClock();
            World = new InMemoryPolicyWorld();
            Handler = new StubHandler();
            Diagnostics = new FreeLlmApiRoutingDiagnostics();
            Options = new FreeLlmApiOptions
            {
                Enabled = true,
                BaseUrl = FreeLlmApiTestRig.BaseUrl,
                ApiKey = $"env:{_keyEnv}",
                DefaultModel = _model,
            };
            World.AddPolicy(_agent, ["agent-default", $"freellmapi:{_model}"], "agent-default");

            var directory = FreeLlmApiWiring.CreateDirectory(World, Options, Clock);
            var secrets = FreeLlmApiWiring.CreateProviderSecretSource(
                Options,
                resolver ?? new BootstrapSecretResolver(name => name == _keyEnv ? _testKeyValue : null),
                new FakeSecrets());

            var http = FreeLlmApiTestRig.Client(Handler);
            var standard = new OpenAiChatAdapter(http, Clock);
            var wrapped = FreeLlmApiWiring.WrapOpenAiAdapter(standard, Options, http, Clock, Diagnostics);
            var adapters = new DictionaryAdapterRegistry(new Dictionary<string, IModelAdapter>(StringComparer.Ordinal)
            {
                [FreeLlmApiProvider.AdapterName] = wrapped,
            });

            Quota = new RetryAfterQuotaTracker(Clock);
            Gateway = new ModelGateway(
                new RouteResolver(World, directory),
                directory,
                adapters,
                secrets,
                new HealthTracker(new BreakerOptions(), Clock),
                new UsageRecorder(new InMemoryUsageStore()),
                quota: Quota,
                time: Clock);
        }

        public FakeClock Clock { get; }

        public InMemoryPolicyWorld World { get; }

        public StubHandler Handler { get; }

        public FreeLlmApiRoutingDiagnostics Diagnostics { get; }

        public RetryAfterQuotaTracker Quota { get; }

        public FreeLlmApiOptions Options { get; }

        public ModelGateway Gateway { get; }

        public Task<List<ModelEvent>> CallAsync(params ModelMessage[] messages) =>
            GatewayHarness.CollectAsync(Gateway.CallAsync(new ModelRequest(_agent, "agent-default", messages, []), Ct), Ct);
    }

    private static ModelMessage User(string text = "Hello") => new(ModelRole.User, text);

    /// <summary>Builds an SSE body with one text delta per item, then a stop frame and the done marker.</summary>
    private static string Sse(params string[] deltas)
    {
        var sb = new StringBuilder();
        foreach (var delta in deltas)
        {
            sb.Append("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"")
                .Append(_model)
                .Append("\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"")
                .Append(delta)
                .Append("\"},\"finish_reason\":null}]}\n\n");
        }

        sb.Append("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"")
            .Append(_model)
            .Append("\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")
            .Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    private static string TextOf(IEnumerable<ModelEvent> events) => OpenAiChatTestRig.TextOf(events);

    [Fact]
    public async Task A_single_chunk_reply_completes_as_one_text_event_and_a_done_that_names_the_served_model()
    {
        var rig = new Rig();
        rig.Handler.Reply(FreeLlmApiTestRig.WithHeaders(Replies.Sse(Sse("Hi")), "chat_direct.headers"));

        var events = await rig.CallAsync(User());

        Assert.Equal("Hi", TextOf(events));
        Assert.Single(events.OfType<ModelEvent.TextDelta>());
        var done = Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Equal(StopReason.EndTurn, done.StopReason);
        Assert.Equal(_model, done.ServedModel);
    }

    [Fact]
    public async Task Streamed_deltas_reach_the_caller_one_by_one_in_order()
    {
        var rig = new Rig();
        rig.Handler.Reply(Replies.Sse(Sse("Hel", "lo", "!")));

        var events = await rig.CallAsync(User());

        Assert.Equal(["Hel", "lo", "!"], events.OfType<ModelEvent.TextDelta>().Select(e => e.Text));
        Assert.IsType<ModelEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task The_call_goes_to_the_configured_base_url_with_the_key_as_a_bearer_token_and_the_configured_model()
    {
        var rig = new Rig();
        rig.Handler.Reply(Replies.Sse(Sse("ok")));

        await rig.CallAsync(User());

        var sent = Assert.Single(rig.Handler.Requests);
        Assert.Equal($"{FreeLlmApiTestRig.BaseUrl}/chat/completions", sent.Url.ToString());
        Assert.Equal($"Bearer {_testKeyValue}", sent.Authorization);
        Assert.Contains($"\"model\":\"{_model}\"", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_call_round_trip_returns_the_call_and_then_takes_the_tool_result_back()
    {
        var rig = new Rig();
        rig.Handler.Reply(Replies.Sse(OpenAiChatTestRig.Fixture("tool_call_split.sse")));
        rig.Handler.Reply(Replies.Sse(FreeLlmApiTestRig.Fixture("chat_direct.sse")));
        var request = new ModelRequest(
            _agent,
            "agent-default",
            [User("Weather in Lisbon?")],
            [new ModelToolDefinition("get_weather", "Looks up the weather", "{\"type\":\"object\"}")]);

        var first = await GatewayHarness.CollectAsync(rig.Gateway.CallAsync(request, Ct), Ct);

        var start = Assert.Single(first.OfType<ModelEvent.ToolCallStart>());
        Assert.Equal("call_abc123", start.CallId);
        Assert.Equal("get_weather", start.Name);
        Assert.Equal("{\"city\":\"Lisbon\"}", string.Concat(first.OfType<ModelEvent.ToolCallArgsDelta>().Select(e => e.ArgumentsFragment)));
        Assert.Equal(StopReason.ToolUse, Assert.IsType<ModelEvent.Done>(first[^1]).StopReason);

        var followUp = new ModelRequest(
            _agent,
            "agent-default",
            [
                User("Weather in Lisbon?"),
                new ModelMessage(ModelRole.Assistant, "", ToolCalls: [new ModelToolCall("call_abc123", "get_weather", "{\"city\":\"Lisbon\"}")]),
                new ModelMessage(ModelRole.Tool, "{\"tempC\":21}", ToolCallId: "call_abc123"),
            ],
            request.Tools);
        var second = await GatewayHarness.CollectAsync(rig.Gateway.CallAsync(followUp, Ct), Ct);

        Assert.Equal("Hi", TextOf(second));
        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.Contains("call_abc123", rig.Handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("tempC", rig.Handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_429_with_retry_after_is_rate_limited_and_blocks_the_key_until_the_retry_time()
    {
        var rig = new Rig();
        rig.Handler.Reply(Replies.Error(HttpStatusCode.TooManyRequests, OpenAiChatTestRig.Fixture("rate_limited_429.json"), retryAfter: "30"));
        rig.Handler.Reply(Replies.Sse(Sse("back")));

        var first = await rig.CallAsync(User());

        var limited = Assert.IsType<ModelEvent.Failure>(Assert.Single(first));
        Assert.Equal("rate_limited", limited.Code);
        Assert.Equal(30, limited.RetryAfterSeconds);

        // Inside the window the key is not called again: the call is refused with the retry time.
        var blocked = await rig.CallAsync(User());
        var refusal = Assert.IsType<ModelEvent.Failure>(Assert.Single(blocked));
        Assert.Equal("no_capacity", refusal.Code);
        Assert.Equal(30, refusal.RetryAfterSeconds);
        Assert.Single(rig.Handler.Requests);

        // After the window the next call goes out and succeeds.
        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        var after = await rig.CallAsync(User());
        Assert.Equal("back", TextOf(after));
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task A_502_mixed_failure_is_a_provider_error_with_no_retry_time_and_its_trail_is_kept()
    {
        var rig = new Rig();
        rig.Handler.Reply(FreeLlmApiTestRig.WithHeaders(
            Replies.Error(HttpStatusCode.BadGateway, "{\"error\":{\"message\":\"all upstreams failed\"}}"),
            "chat_fallback.headers"));

        var events = await rig.CallAsync(User());

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(events));
        Assert.Equal("provider_error", failure.Code);
        Assert.Null(failure.RetryAfterSeconds);
        Assert.Equal(["llama-3.3-70b", "gemini-2.5-flash"], rig.Diagnostics.Latest?.TrailModels);
        Assert.Equal(1, rig.Diagnostics.Calls);
        Assert.Equal(1, rig.Diagnostics.FallbackCalls);
    }

    [Fact]
    public async Task The_routing_headers_of_a_direct_call_are_recorded_with_no_fallback()
    {
        var rig = new Rig();
        rig.Handler.Reply(FreeLlmApiTestRig.WithHeaders(Replies.Sse(Sse("Hi")), "chat_direct.headers"));

        await rig.CallAsync(User());

        Assert.Equal(_model, rig.Diagnostics.Latest?.RoutedModel);
        Assert.Empty(rig.Diagnostics.Latest!.Trail);
        Assert.Equal(0, rig.Diagnostics.FallbackCalls);
    }

    [Fact]
    public async Task The_key_never_appears_in_an_event_on_the_success_or_the_failure_path()
    {
        const string planted = "PLANTED-KEY-VALUE-FOR-LEAK-CHECK";
        var rig = new Rig(new BootstrapSecretResolver(name => name == _keyEnv ? planted : null));
        rig.Handler.Reply(Replies.Sse(Sse("fine")));
        rig.Handler.Reply(FreeLlmApiTestRig.WithHeaders(
            Replies.Error(HttpStatusCode.BadGateway, "{\"error\":{\"message\":\"upstream said no\"}}"),
            "chat_fallback.headers"));

        var ok = await rig.CallAsync(User());
        var failed = await rig.CallAsync(User());

        // The key reached the wire as the bearer, which is the only place it may go.
        Assert.Equal($"Bearer {planted}", rig.Handler.Requests[0].Authorization);
        var everything = string.Concat(ok.Concat(failed).Select(e => e.ToString()));
        Assert.DoesNotContain(planted, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(planted, rig.Diagnostics.Latest?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_that_cannot_be_read_at_call_time_skips_the_provider_without_calling_it()
    {
        var rig = new Rig(new BootstrapSecretResolver(_ => null));

        var events = await rig.CallAsync(User());

        Assert.Equal("no_capacity", Assert.IsType<ModelEvent.Failure>(Assert.Single(events)).Code);
        Assert.Empty(rig.Handler.Requests);
    }
}
