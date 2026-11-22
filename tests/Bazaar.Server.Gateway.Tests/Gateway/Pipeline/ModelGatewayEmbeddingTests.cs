using System.Net;
using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.Ai;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Tests.Gateway.Adapters.OpenAi;

namespace Bazaar.Server.Tests.Gateway.Pipeline;

/// <summary>An adapter that can embed: plays queued outcomes and remembers the endpoints and requests it saw.</summary>
internal sealed class ScriptedEmbeddingAdapter : IModelAdapter, IEmbeddingAdapter
{
    private readonly Queue<EmbeddingOutcome> _outcomes = new();

    public List<ProviderEndpoint> Endpoints { get; } = [];

    public List<EmbeddingRequest> Requests { get; } = [];

    public AdapterKind Kind => AdapterKind.OpenAiChat;

    public void Enqueue(EmbeddingOutcome outcome) => _outcomes.Enqueue(outcome);

    public IAsyncEnumerable<ModelEvent> StreamAsync(ProviderEndpoint endpoint, ModelRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<EmbeddingOutcome> EmbedAsync(ProviderEndpoint endpoint, EmbeddingRequest request, CancellationToken ct)
    {
        Endpoints.Add(endpoint);
        Requests.Add(request);
        return Task.FromResult(_outcomes.Dequeue());
    }
}

public class ModelGatewayEmbeddingTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static EmbeddingRequest Request(string route = "r", params string[] inputs) =>
        new(GatewayHarness.Agent, route, inputs.Length == 0 ? ["one", "two"] : inputs);

    private static EmbeddingOutcome.Succeeded Ok(int count = 2, string? served = "m", long tokens = 7) =>
        new EmbeddingOutcome.Succeeded([.. Enumerable.Range(0, count).Select(i => new[] { i + 0.5f, 1f })], served, tokens);

    private static EmbeddingOutcome.Failed Fail(string code) => new EmbeddingOutcome.Failed(new ModelEvent.Failure(code, "boom"));

    private static ScriptedEmbeddingAdapter Embedder(GatewayHarness h, string id)
    {
        var adapter = new ScriptedEmbeddingAdapter();
        h.Adapters[id] = adapter;
        h.EmbeddingModel(id);
        return adapter;
    }

    [Fact]
    public async Task Success_returns_one_vector_per_input_and_writes_one_usage_row()
    {
        var h = new GatewayHarness();
        var a = Embedder(h, "a");
        a.Enqueue(Ok());

        var result = await h.Gateway().EmbedAsync(Request(), _ct);

        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal(0.5f, result.Vectors[0][0]);
        Assert.Equal("m", result.ModelId);
        Assert.Equal(7, result.InputTokens);
        var row = Assert.Single(h.Usage.Records);
        Assert.Equal(UsageStatus.Success, row.Status);
        Assert.Equal(("a", "m", GatewayHarness.SecretRef("a")), (row.ProviderId, row.ModelId, row.KeyRef));
        Assert.Equal(7, row.Tokens.Input);
        Assert.Equal(0, row.Tokens.Output);
        var endpoint = Assert.Single(a.Endpoints);
        Assert.Equal("m", endpoint.ModelId);
        Assert.Equal(FakeSecrets.Sentinel, endpoint.ApiKey);
    }

    [Fact]
    public async Task A_model_the_policy_does_not_allow_is_denied_and_never_called()
    {
        var h = new GatewayHarness();
        var c = Embedder(h, "c");

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() => h.Gateway().EmbedAsync(Request("c:m"), _ct));

        Assert.Equal("policy_denied", ex.Code);
        Assert.Empty(c.Requests);
        Assert.Empty(h.Usage.Records);
    }

    [Fact]
    public async Task An_adapter_that_cannot_embed_is_skipped_and_the_next_candidate_serves()
    {
        var h = new GatewayHarness();
        h.EmbeddingModel("a");
        var b = Embedder(h, "b");
        b.Enqueue(Ok());

        var result = await h.Gateway().EmbedAsync(Request(), _ct);

        Assert.Equal(2, result.Vectors.Count);
        Assert.Empty(h.Adapter("a").Provider.Calls);
        var row = Assert.Single(h.Usage.Records);
        Assert.Equal("b", row.ProviderId);
    }

    [Fact]
    public async Task When_no_candidate_can_embed_the_call_throws_with_a_clear_message_and_writes_no_row()
    {
        var h = new GatewayHarness().EmbeddingModel("a").EmbeddingModel("b");

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() => h.Gateway().EmbedAsync(Request(), _ct));

        Assert.Equal("no_capacity", ex.Code);
        Assert.Contains("cannot embed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Usage.Records);
    }

    [Fact]
    public async Task A_retryable_failure_falls_back_to_the_next_allowed_candidate_with_a_row_for_each_attempt()
    {
        var h = new GatewayHarness();
        Embedder(h, "a").Enqueue(Fail("rate_limited"));
        Embedder(h, "b").Enqueue(Ok());

        var result = await h.Gateway().EmbedAsync(Request(), _ct);

        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal(["a", "b"], h.Usage.Records.Select(r => r.ProviderId));
        Assert.Equal([UsageStatus.Error, UsageStatus.Success], h.Usage.Records.Select(r => r.Status));
        Assert.Single(h.Quota.Reports);
    }

    [Fact]
    public async Task Fallback_never_reaches_a_model_outside_policy()
    {
        var h = new GatewayHarness();
        Embedder(h, "a").Enqueue(Fail("provider_error"));
        var c = Embedder(h, "c");
        h.World.AddRoute("wide", ["a:m", "c:m"]);
        h.World.AddPolicy("narrow", ["a:m", "wide"], "wide");

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() =>
            h.Gateway().EmbedAsync(new EmbeddingRequest("narrow", "wide", ["x"]), _ct));

        Assert.Equal("provider_error", ex.Code);
        Assert.Empty(c.Requests);
        Assert.Equal("a", Assert.Single(h.Usage.Records).ProviderId);
    }

    [Fact]
    public async Task A_non_retryable_failure_stops_without_fallback()
    {
        var h = new GatewayHarness();
        Embedder(h, "a").Enqueue(Fail("invalid_request"));
        var b = Embedder(h, "b");

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() => h.Gateway().EmbedAsync(Request(), _ct));

        Assert.Equal("invalid_request", ex.Code);
        Assert.Empty(b.Requests);
        Assert.Single(h.Usage.Records);
    }

    [Fact]
    public async Task A_wrong_vector_count_is_a_provider_error_and_falls_back()
    {
        var h = new GatewayHarness();
        Embedder(h, "a").Enqueue(Ok(count: 1));
        Embedder(h, "b").Enqueue(Ok());

        var result = await h.Gateway().EmbedAsync(Request(), _ct);

        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal([UsageStatus.Error, UsageStatus.Success], h.Usage.Records.Select(r => r.Status));
    }

    [Fact]
    public async Task An_empty_batch_returns_an_empty_result_without_a_call()
    {
        var h = new GatewayHarness();
        var a = Embedder(h, "a");

        var result = await h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "r", []), _ct);

        Assert.Empty(result.Vectors);
        Assert.Empty(a.Requests);
        Assert.Empty(h.Usage.Records);
    }

    [Fact]
    public async Task Missing_usage_falls_back_to_the_estimated_input_size()
    {
        var h = new GatewayHarness();
        Embedder(h, "a").Enqueue(Ok(tokens: 0));

        var result = await h.Gateway().EmbedAsync(Request("r", "12345678", "1234"), _ct);

        Assert.Equal(3, result.InputTokens);
        Assert.Equal(3, Assert.Single(h.Usage.Records).Tokens.Input);
    }

    [Fact]
    public async Task The_openai_adapter_posts_to_embeddings_and_the_gateway_records_the_usage()
    {
        var h = new GatewayHarness();
        var handler = new StubHandler();
        handler.Reply(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"model":"text-embed-1","data":[{"index":1,"embedding":[0.5,0.25]},{"index":0,"embedding":[1,2]}],"usage":{"prompt_tokens":9,"total_tokens":9}}""",
                Encoding.UTF8,
                "application/json"),
        }));
        h.Adapters["a"] = new OpenAiChatAdapter(new HttpClient(handler));
        h.EmbeddingModel("a");
        h.World.Providers["a"] = h.World.Providers["a"] with { BaseUrl = "https://example.test/v1/" };

        var result = await h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "r", ["x", "y"], Dimensions: 2), _ct);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://example.test/v1/embeddings", sent.Url.ToString());
        Assert.Equal($"Bearer {FakeSecrets.Sentinel}", sent.Authorization);
        Assert.Contains("\"model\":\"m\"", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"input\":[\"x\",\"y\"]", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"dimensions\":2", sent.Body, StringComparison.Ordinal);
        Assert.Equal([1f, 2f], result.Vectors[0]);
        Assert.Equal([0.5f, 0.25f], result.Vectors[1]);
        Assert.Equal("text-embed-1", result.ModelId);
        var row = Assert.Single(h.Usage.Records);
        Assert.Equal((UsageStatus.Success, 9L, "text-embed-1"), (row.Status, row.Tokens.Input, row.ServedModel));
    }

    [Fact]
    public async Task An_http_429_from_the_openai_adapter_is_classified_and_falls_back()
    {
        var h = new GatewayHarness();
        var handler = new StubHandler();
        handler.Reply(Replies.Error(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""", "3"));
        h.Adapters["a"] = new OpenAiChatAdapter(new HttpClient(handler));
        h.EmbeddingModel("a");
        Embedder(h, "b").Enqueue(Ok());

        var result = await h.Gateway().EmbedAsync(Request(), _ct);

        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal("rate_limited", Assert.Single(h.Quota.Reports).Failure.Code);
        Assert.Equal(["a", "b"], h.Usage.Records.Select(r => r.ProviderId));
    }

    [Fact]
    public async Task A_malformed_openai_response_is_a_provider_error()
    {
        var h = new GatewayHarness();
        var handler = new StubHandler();
        handler.Reply(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") }));
        h.Adapters["a"] = new OpenAiChatAdapter(new HttpClient(handler));
        h.EmbeddingModel("a");
        h.World.AddPolicy("solo", ["a:m"], "a:m");

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() =>
            h.Gateway().EmbedAsync(new EmbeddingRequest("solo", "a:m", ["x"]), _ct));

        Assert.Equal("provider_error", ex.Code);
    }

    [Fact]
    public async Task The_anthropic_and_gemini_adapters_do_not_claim_to_embed()
    {
        await Task.CompletedTask;
        Assert.False(typeof(IEmbeddingAdapter).IsAssignableFrom(typeof(Bazaar.Server.Gateway.Adapters.Anthropic.AnthropicMessagesAdapter)));
        Assert.False(typeof(IEmbeddingAdapter).IsAssignableFrom(typeof(Bazaar.Server.Gateway.Adapters.Gemini.GeminiGenerateContentAdapter)));
        Assert.True(typeof(IEmbeddingAdapter).IsAssignableFrom(typeof(OpenAiChatAdapter)));
    }
}

public class FakeModelGatewayEmbeddingTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static FakeModelGateway Gateway() => new(new FakeProvider(TimeProvider.System));

    [Fact]
    public async Task The_same_text_gives_the_same_unit_vector_and_the_default_size()
    {
        var gateway = Gateway();

        var first = await gateway.EmbedAsync(new EmbeddingRequest("x", "m", ["alpha"]), _ct);
        var second = await gateway.EmbedAsync(new EmbeddingRequest("x", "m", ["alpha"]), _ct);

        Assert.Equal(first.Vectors[0], second.Vectors[0]);
        Assert.Equal(FakeModelGateway.DefaultDimensions, first.Vectors[0].Length);
        Assert.Equal(1.0, Math.Sqrt(first.Vectors[0].Sum(v => (double)v * v)), 4);
        Assert.Equal("m", first.ModelId);
    }

    [Fact]
    public async Task Different_texts_differ_and_a_batch_matches_single_calls()
    {
        var gateway = Gateway();

        var batch = await gateway.EmbedAsync(new EmbeddingRequest("x", "m", ["alpha", "beta"]), _ct);
        var single = await gateway.EmbedAsync(new EmbeddingRequest("x", "m", ["beta"]), _ct);

        Assert.Equal(2, batch.Vectors.Count);
        Assert.NotEqual(batch.Vectors[0], batch.Vectors[1]);
        Assert.Equal(single.Vectors[0], batch.Vectors[1]);
    }

    [Fact]
    public async Task Dimensions_are_honoured_and_requests_are_recorded()
    {
        var gateway = Gateway();

        var result = await gateway.EmbedAsync(new EmbeddingRequest("x", "m", ["a", "b", "c"], Dimensions: 5), _ct);

        Assert.All(result.Vectors, v => Assert.Equal(5, v.Length));
        Assert.Equal(3, Assert.Single(gateway.EmbeddingRequests).Inputs.Count);
    }

    [Fact]
    public async Task An_empty_batch_gives_no_vectors()
    {
        var result = await Gateway().EmbedAsync(new EmbeddingRequest("x", "m", []), _ct);

        Assert.Empty(result.Vectors);
    }
}
