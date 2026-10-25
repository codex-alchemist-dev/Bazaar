using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.Gemini;

namespace Bazaar.Server.Tests.Gateway.Adapters.Gemini;

public class GeminiGenerateContentAdapterTests
{
    private const string _baseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private const string _key = "fake-key-for-tests";
    private const string _modelId = "gemini-2.5-flash";

    private static readonly ProviderEndpoint _endpoint = new(_baseUrl, _modelId, _key);

    private static readonly ModelRequest _simple = new("agent-1", _modelId, [new ModelMessage(ModelRole.User, "Hi")], []);

    private const string _retryInfoBody = """
        {"error": {"code": 429, "message": "Resource has been exhausted (e.g. check quota).", "status": "RESOURCE_EXHAUSTED",
          "details": [
            {"@type": "type.googleapis.com/google.rpc.QuotaFailure", "violations": [{"quotaMetric": "generate_content_free_tier_requests"}]},
            {"@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "32s"}
          ]}}
        """;

    [Fact]
    public async Task Text_stream_yields_text_then_usage_and_done()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("text_stream.sse")));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.TextDelta("The capital of France"),
            new ModelEvent.TextDelta(" is Paris"),
            new ModelEvent.TextDelta("."),
            new ModelEvent.UsageReported(new GatewayTokenUsage(12, 0, 0, 0, 6, 0)),
            new ModelEvent.Done(StopReason.EndTurn, "gemini-2.5-flash-001"),
        ], events);
    }

    [Fact]
    public async Task Thinking_parts_become_thinking_deltas_and_count_as_output()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("thinking_stream.sse")));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.ThinkingDelta("Add 17 and 25 first."),
            new ModelEvent.ThinkingDelta("17 + 25 = 42, so the answer is 42."),
            new ModelEvent.TextDelta("The sum is 42."),
            // Output is candidates (4) plus thoughts (9); Thinking is the thoughts subset.
            new ModelEvent.UsageReported(new GatewayTokenUsage(20, 0, 0, 0, 13, 9)),
            new ModelEvent.Done(StopReason.EndTurn, "gemini-2.5-pro"),
        ], events);
    }

    [Fact]
    public async Task Thought_signature_on_a_function_call_is_surfaced_as_provider_data()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("function_call_signature.sse")));

        var events = await RunAsync(handler);

        Assert.Equal(new ModelEvent.ToolCallStart("call_0", "get_weather", "c2lnLWFiYw=="), events[0]);
    }

    [Fact]
    public async Task Provider_data_is_sent_back_as_thought_signature_on_the_function_call_part()
    {
        var request = new ModelRequest(
            "agent-1",
            _modelId,
            [
                new ModelMessage(ModelRole.User, "Weather?"),
                new ModelMessage(ModelRole.Assistant, "", ToolCalls: [new ModelToolCall("call_0", "get_weather", "{\"city\":\"Paris\"}", "c2lnLWFiYw==")]),
                new ModelMessage(ModelRole.Tool, "{\"temp_c\":18}", ToolCallId: "call_0"),
            ],
            []);
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("text_stream.sse")));

        await RunAsync(handler, request);

        var part = JsonNode.Parse(handler.Body!)!["contents"]![1]!["parts"]![0]!;
        Assert.Equal("c2lnLWFiYw==", part["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("get_weather", part["functionCall"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Function_call_is_assembled_whole_with_synthesized_ids_and_tool_use_stop()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("function_call_stream.sse")));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.TextDelta("Let me check."),
            new ModelEvent.ToolCallStart("call_0", "get_weather"),
            new ModelEvent.ToolCallArgsDelta("call_0", "{\"city\":\"Paris\"}"),
            new ModelEvent.ToolCallEnd("call_0"),
            new ModelEvent.ToolCallStart("call_1", "get_time"),
            new ModelEvent.ToolCallArgsDelta("call_1", "{\"zone\":\"Europe/Paris\"}"),
            new ModelEvent.ToolCallEnd("call_1"),
            new ModelEvent.UsageReported(new GatewayTokenUsage(30, 0, 0, 0, 14, 0)),
            new ModelEvent.Done(StopReason.ToolUse, _modelId),
        ], events);
    }

    [Fact]
    public async Task Synthesized_call_ids_restart_for_each_response()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("function_call_stream.sse")));
        using var http = new HttpClient(handler);
        var adapter = new GeminiGenerateContentAdapter(http);

        var first = await CollectAsync(adapter, _endpoint, _simple, TestContext.Current.CancellationToken);
        var second = await CollectAsync(adapter, _endpoint, _simple, TestContext.Current.CancellationToken);

        Assert.Equal("call_0", ((ModelEvent.ToolCallStart)first.First(e => e is ModelEvent.ToolCallStart)).CallId);
        Assert.Equal("call_0", ((ModelEvent.ToolCallStart)second.First(e => e is ModelEvent.ToolCallStart)).CallId);
    }

    [Fact]
    public async Task Cached_content_tokens_are_read_and_excluded_from_input()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("cached_content_stream.sse")));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.TextDelta("Cached answer."),
            // 1500 prompt tokens, 1200 of them cached: 300 uncached input, 1200 cache read.
            new ModelEvent.UsageReported(new GatewayTokenUsage(300, 0, 0, 1200, 40, 0)),
            new ModelEvent.Done(StopReason.EndTurn, _modelId),
        ], events);
    }

    [Fact]
    public async Task Blocked_prompt_warns_then_ends_with_content_filter()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("blocked_prompt.sse")));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.Warning("prompt_blocked", "The provider blocked the prompt (SAFETY)."),
            new ModelEvent.UsageReported(new GatewayTokenUsage(25, 0, 0, 0, 0, 0)),
            // No modelVersion in the blocked chunk, so the served model falls back to the model id.
            new ModelEvent.Done(StopReason.ContentFilter, _modelId),
        ], events);
    }

    [Theory]
    [InlineData("STOP", StopReason.EndTurn)]
    [InlineData("MAX_TOKENS", StopReason.MaxTokens)]
    [InlineData("SAFETY", StopReason.ContentFilter)]
    [InlineData("RECITATION", StopReason.ContentFilter)]
    [InlineData("BLOCKLIST", StopReason.ContentFilter)]
    [InlineData("PROHIBITED_CONTENT", StopReason.ContentFilter)]
    [InlineData("SPII", StopReason.ContentFilter)]
    [InlineData("MALFORMED_FUNCTION_CALL", StopReason.Other)]
    public async Task Finish_reason_maps_to_stop_reason(string finish, StopReason expected)
    {
        var sse = """data: {"candidates":[{"content":{"parts":[{"text":"x"}]},"finishReason":"__FINISH__"}]}""".Replace("__FINISH__", finish) + "\n\n";
        var handler = new StubHandler((_, _) => Task.FromResult(Text(sse)));

        var events = await RunAsync(handler);

        Assert.Equal(new ModelEvent.Done(expected, _modelId), events[^1]);
    }

    [Fact]
    public async Task Stream_that_ends_without_finish_reason_is_dropped()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("dropped_stream.sse")));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.TextDelta("Partial reply"),
            new ModelEvent.TextDelta(" that never"),
            new ModelEvent.UsageReported(new GatewayTokenUsage(9, 0, 0, 0, 4, 0)),
            new ModelEvent.Failure("stream_dropped", "The stream ended before the response finished."),
        ], events);
    }

    [Fact]
    public async Task Connection_cut_mid_body_is_a_dropped_stream()
    {
        var first = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Half\"}]}}]}\n\n";
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CutStream(Encoding.UTF8.GetBytes(first))),
        }));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.TextDelta("Half"),
            new ModelEvent.Failure("stream_dropped", "The stream ended before the response finished."),
        ], events);
    }

    [Fact]
    public async Task In_stream_error_keeps_partial_output_and_reports_failure()
    {
        var sse = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Par\"}]}}]}\n\n"
            + "data: {\"error\":{\"code\":500,\"message\":\"Internal error mid-stream.\",\"status\":\"INTERNAL\"}}\n\n";
        var handler = new StubHandler((_, _) => Task.FromResult(Text(sse)));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.TextDelta("Par"),
            new ModelEvent.Failure("provider_error", "Internal error mid-stream."),
        ], events);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public async Task Chunk_that_is_not_a_json_object_is_a_provider_error(string data)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Text($"data: {data}\n\n")));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.Failure("provider_error", "The stream sent a chunk that is not a JSON object."),
        ], events);
    }

    [Fact]
    public async Task Request_body_maps_system_roles_tools_and_tool_results()
    {
        var request = new ModelRequest(
            "agent-1",
            _modelId,
            [
                new ModelMessage(ModelRole.System, "Be brief."),
                new ModelMessage(ModelRole.System, "Use metric units."),
                new ModelMessage(ModelRole.User, "Weather and time in Paris?"),
                new ModelMessage(ModelRole.Assistant, "Checking.", ToolCalls:
                [
                    new ModelToolCall("tc_a", "get_weather", "{\"city\":\"Paris\"}"),
                    new ModelToolCall("tc_b", "get_time", "{\"zone\":\"Europe/Paris\"}"),
                ]),
                new ModelMessage(ModelRole.Tool, "{\"temp_c\":18}", ToolCallId: "tc_a"),
                new ModelMessage(ModelRole.Tool, "plain text, not json", ToolCallId: "tc_b"),
            ],
            [new ModelToolDefinition("get_weather", "Current weather.", "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}")],
            MaxOutputTokens: 256,
            Temperature: 0.2);
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("text_stream.sse")));

        await RunAsync(handler, request);

        var body = JsonNode.Parse(handler.Body!)!;
        var system = body["systemInstruction"]!["parts"]!.AsArray();
        Assert.Equal(2, system.Count);
        Assert.Equal("Be brief.", system[0]!["text"]!.GetValue<string>());
        Assert.Equal("Use metric units.", system[1]!["text"]!.GetValue<string>());

        // The two tool results follow the model turn, so they merge into one user turn.
        var contents = body["contents"]!.AsArray();
        Assert.Equal(3, contents.Count);
        Assert.Equal("user", contents[0]!["role"]!.GetValue<string>());
        Assert.Equal("model", contents[1]!["role"]!.GetValue<string>());
        Assert.Equal("user", contents[2]!["role"]!.GetValue<string>());

        var modelParts = contents[1]!["parts"]!.AsArray();
        Assert.Equal("Checking.", modelParts[0]!["text"]!.GetValue<string>());
        Assert.Equal("get_weather", modelParts[1]!["functionCall"]!["name"]!.GetValue<string>());
        Assert.Equal("Paris", modelParts[1]!["functionCall"]!["args"]!["city"]!.GetValue<string>());
        Assert.Equal("get_time", modelParts[2]!["functionCall"]!["name"]!.GetValue<string>());
        Assert.Equal("Europe/Paris", modelParts[2]!["functionCall"]!["args"]!["zone"]!.GetValue<string>());

        var resultParts = contents[2]!["parts"]!.AsArray();
        Assert.Equal(2, resultParts.Count);
        Assert.Equal("get_weather", resultParts[0]!["functionResponse"]!["name"]!.GetValue<string>());
        Assert.Equal(18, resultParts[0]!["functionResponse"]!["response"]!["temp_c"]!.GetValue<int>());
        Assert.Equal("get_time", resultParts[1]!["functionResponse"]!["name"]!.GetValue<string>());
        Assert.Equal("plain text, not json", resultParts[1]!["functionResponse"]!["response"]!["result"]!.GetValue<string>());

        var declaration = body["tools"]!.AsArray()[0]!["functionDeclarations"]!.AsArray()[0]!;
        Assert.Equal("get_weather", declaration["name"]!.GetValue<string>());
        Assert.Equal("Current weather.", declaration["description"]!.GetValue<string>());
        Assert.Equal("object", declaration["parametersJsonSchema"]!["type"]!.GetValue<string>());

        Assert.Equal(256, body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(0.2, body["generationConfig"]!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task Key_travels_in_header_never_in_url_and_extra_headers_apply()
    {
        var endpoint = new ProviderEndpoint(_baseUrl, _modelId, _key, new Dictionary<string, string> { ["x-test-extra"] = "on" });
        var handler = new StubHandler((_, _) => Task.FromResult(Sse("text_stream.sse")));

        await RunAsync(handler, endpoint: endpoint);

        Assert.Equal($"{_baseUrl}/models/{_modelId}:streamGenerateContent?alt=sse", handler.Uri!.ToString());
        Assert.DoesNotContain(_key, handler.Uri!.ToString());
        Assert.DoesNotContain(_key, handler.Body!);
        Assert.Equal(_key, handler.ApiKeyHeader);
        Assert.Equal("on", handler.ExtraHeader);
    }

    [Fact]
    public async Task Rate_limit_reads_retry_info_delay()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, _retryInfoBody)));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.Failure("rate_limited", "Resource has been exhausted (e.g. check quota).", 32),
        ], events);
    }

    [Fact]
    public async Task Retry_after_header_wins_over_retry_info()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, _retryInfoBody, retryAfter: "7")));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.Failure("rate_limited", "Resource has been exhausted (e.g. check quota).", 7),
        ], events);
    }

    [Fact]
    public async Task Billing_quota_text_is_a_spend_cap_not_a_rate_limit()
    {
        const string body = """
            {"error": {"code": 429, "message": "Your project has exceeded its monthly spend cap. Check your billing settings.", "status": "RESOURCE_EXHAUSTED"}}
            """;
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, body, retryAfter: "30")));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>(
        [
            new ModelEvent.Failure("spend_capped", "Your project has exceeded its monthly spend cap. Check your billing settings."),
        ], events);
    }

    [Fact]
    public async Task Server_error_carries_the_provider_message()
    {
        const string body = """
            {"error": {"code": 500, "message": "Internal error encountered.", "status": "INTERNAL"}}
            """;
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, body)));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>([new ModelEvent.Failure("provider_error", "Internal error encountered.")], events);
    }

    [Fact]
    public async Task Non_json_error_body_falls_back_to_a_status_message()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html>Unavailable</html>", Encoding.UTF8, "text/html"),
        }));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>([new ModelEvent.Failure("provider_error", "The provider returned HTTP 503.")], events);
    }

    [Fact]
    public async Task Http_timeout_becomes_a_timeout_failure()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Sse("text_stream.sse");
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var adapter = new GeminiGenerateContentAdapter(http);

        var events = await CollectAsync(adapter, _endpoint, _simple, TestContext.Current.CancellationToken);

        Assert.Equal<ModelEvent>([new ModelEvent.Failure("timeout", "The provider did not respond in time.")], events);
    }

    [Fact]
    public async Task Unreachable_provider_becomes_a_provider_error()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("Connection refused."));

        var events = await RunAsync(handler);

        Assert.Equal<ModelEvent>([new ModelEvent.Failure("provider_error", "The provider could not be reached.")], events);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(async (_, ct) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Sse("text_stream.sse");
        });

        using var http = new HttpClient(handler);
        var adapter = new GeminiGenerateContentAdapter(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(adapter, _endpoint, _simple, cancellation.Token));
    }

    private static async Task<List<ModelEvent>> RunAsync(
        StubHandler handler,
        ModelRequest? request = null,
        ProviderEndpoint? endpoint = null)
    {
        using var http = new HttpClient(handler);
        var adapter = new GeminiGenerateContentAdapter(http);
        return await CollectAsync(adapter, endpoint ?? _endpoint, request ?? _simple, TestContext.Current.CancellationToken);
    }

    private static async Task<List<ModelEvent>> CollectAsync(
        GeminiGenerateContentAdapter adapter,
        ProviderEndpoint endpoint,
        ModelRequest request,
        CancellationToken ct)
    {
        var events = new List<ModelEvent>();
        await foreach (var evt in adapter.StreamAsync(endpoint, request, ct))
        {
            events.Add(evt);
        }

        return events;
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Gateway", "Adapters", "Gemini", "Fixtures", name);

    private static HttpResponseMessage Sse(string fixture) => Text(File.ReadAllText(FixturePath(fixture)));

    private static HttpResponseMessage Text(string sse) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }

    /// <summary>Records what the adapter sent and answers with a scripted response.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? ApiKeyHeader { get; private set; }

        public string? ExtraHeader { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            ApiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var keys) ? keys.Single() : null;
            ExtraHeader = request.Headers.TryGetValues("x-test-extra", out var extras) ? extras.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request, cancellationToken);
        }
    }

    /// <summary>Yields a prefix, then fails the way a reset connection does.</summary>
    private sealed class CutStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= prefix.Length)
            {
                throw new IOException("Connection reset by peer.");
            }

            var n = Math.Min(count, prefix.Length - _position);
            Array.Copy(prefix, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
