using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Ai;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Tests.Gateway.Pipeline;

/// <summary>Doc 05 Embeddings: kind exclusion, install route setting, dimension check, chunking and input caps.</summary>
public class ModelGatewayEmbeddingCapsTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static EmbeddingOutcome.Succeeded Vectors(int count, int dims = 2) =>
        new([.. Enumerable.Range(0, count).Select(i => Enumerable.Repeat(i + 1f, dims).ToArray())], "m", count);

    private static (GatewayHarness H, ScriptedEmbeddingAdapter A) Setup(int? maxBatch = null, int? maxChars = null)
    {
        var h = new GatewayHarness().EmbeddingModel("a", maxBatch, maxChars);
        var a = new ScriptedEmbeddingAdapter();
        h.Adapters["a"] = a;
        h.World.AddPolicy(GatewayHarness.Agent, ["a:m", "b:m", "r"], "a:m");
        return (h, a);
    }

    private static ModelRequest Req(string route) => InMemoryPolicyWorldRequest(route);

    private static ModelRequest InMemoryPolicyWorldRequest(string route) => GatewayHarness.Request(route);

    // --- kind exclusion ---

    [Fact]
    public async Task Chat_resolution_excludes_embedding_models()
    {
        var h = new GatewayHarness().EmbeddingModel("a");
        var resolver = h.World.Resolver();

        var single = await resolver.ResolveAsync(Req("a:m"), _ct);
        var route = await resolver.ResolveAsync(Req("r"), _ct);

        Assert.Equal(DenyReason.KindMismatch, Assert.IsType<RouteResolution.Denied>(single).Reason);
        var allowed = Assert.IsType<RouteResolution.Allowed>(route);
        Assert.Equal(["b:m"], allowed.Route.Candidates.Select(c => c.Model.ToString()));
        Assert.Contains(allowed.Dropped, d => d.Reason == DenyReason.KindMismatch);
    }

    [Fact]
    public async Task Embedding_resolution_excludes_chat_models()
    {
        var h = new GatewayHarness().EmbeddingModel("a");
        var resolver = h.World.Resolver();

        var single = await resolver.ResolveAsync(Req("b:m"), GwModelKind.Embedding, _ct);
        var route = await resolver.ResolveAsync(Req("r"), GwModelKind.Embedding, _ct);

        Assert.Equal(DenyReason.KindMismatch, Assert.IsType<RouteResolution.Denied>(single).Reason);
        var allowed = Assert.IsType<RouteResolution.Allowed>(route);
        Assert.Equal(["a:m"], allowed.Route.Candidates.Select(c => c.Model.ToString()));
    }

    [Fact]
    public async Task Embedding_health_is_tracked_apart_from_chat()
    {
        var (h, a) = Setup();
        a.Enqueue(new EmbeddingOutcome.Failed(new ModelEvent.Failure("provider_error", "x")));

        await Assert.ThrowsAsync<GatewayModelException>(() => h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "a:m", ["x"]), _ct));

        Assert.Single(h.Quota.Reports);
        Assert.StartsWith("embedding:", h.Quota.Reports[0].Class, StringComparison.Ordinal);
        Assert.NotEqual(GatewayHarness.KeyOf("a"), h.Quota.Reports[0].Key);
    }

    // --- install route ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task An_omitted_route_with_the_setting_unset_throws_embedding_route_unset(string? route)
    {
        var (h, a) = Setup();

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() =>
            h.Gateway(settings: new FakeSettingStore("")).EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, route, ["x"]), _ct));

        Assert.Equal("embedding_route_unset", ex.Code);
        Assert.Empty(a.Requests);
    }

    [Fact]
    public async Task Without_a_settings_store_an_omitted_route_is_unset()
    {
        var (h, _) = Setup();

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() =>
            h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, null, ["x"]), _ct));

        Assert.Equal("embedding_route_unset", ex.Code);
    }

    [Fact]
    public async Task An_omitted_route_uses_the_setting()
    {
        var (h, a) = Setup();
        a.Enqueue(Vectors(1));

        var result = await h.Gateway(settings: new FakeSettingStore("a:m")).EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, null, ["x"]), _ct);

        Assert.Single(result.Vectors);
        Assert.Equal("a:m", Assert.Single(a.Requests).Route);
    }

    [Fact]
    public async Task The_setting_route_still_goes_through_agent_policy()
    {
        var (h, a) = Setup();
        h.World.AddPolicy("other", ["b:m"], "b:m");

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() =>
            h.Gateway(settings: new FakeSettingStore("a:m")).EmbedAsync(new EmbeddingRequest("other", null, ["x"]), _ct));

        Assert.Equal("policy_denied", ex.Code);
        Assert.Empty(a.Requests);
    }

    [Fact]
    public void The_route_setting_is_registered_with_an_empty_default_and_validated()
    {
        Assert.Contains(GatewaySettings.EmbeddingRoute, GatewaySettings.All);
        Assert.Equal("", GatewaySettings.EmbeddingRoute.Default);
        Assert.Null(GatewaySettings.EmbeddingRoute.CheckValue(System.Text.Json.JsonElement.Parse("\"a:m\"")));
        Assert.NotNull(GatewaySettings.EmbeddingRoute.CheckValue(System.Text.Json.JsonElement.Parse("\"auto/x\"")));
    }

    // --- dimension check ---

    [Fact]
    public async Task A_dimension_mismatch_fails_closed_and_returns_nothing()
    {
        var (h, a) = Setup();
        a.Enqueue(Vectors(2, dims: 3));

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() =>
            h.Gateway(profile: new FixedEmbeddingProfile(4)).EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "a:m", ["x", "y"]), _ct));

        Assert.Equal("embedding_dimension_mismatch", ex.Code);
        var row = Assert.Single(h.Usage.Records);
        Assert.Equal(UsageStatus.Error, row.Status);
    }

    [Fact]
    public async Task A_matching_dimension_passes()
    {
        var (h, a) = Setup();
        a.Enqueue(Vectors(2, dims: 4));

        var result = await h.Gateway(profile: new FixedEmbeddingProfile(4)).EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "a:m", ["x", "y"]), _ct);

        Assert.Equal(2, result.Vectors.Count);
    }

    [Fact]
    public async Task No_registered_profile_means_no_check()
    {
        var (h, a) = Setup();
        a.Enqueue(Vectors(1, dims: 7));

        var result = await h.Gateway(profile: new FixedEmbeddingProfile(null)).EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "a:m", ["x"]), _ct);

        Assert.Single(result.Vectors);
    }

    // --- chunking and caps ---

    [Fact]
    public async Task A_batch_is_split_at_the_cap_in_order_with_one_usage_row_per_chunk()
    {
        var (h, a) = Setup(maxBatch: 2);
        a.Enqueue(Vectors(2));
        a.Enqueue(Vectors(2));
        a.Enqueue(Vectors(1));

        var result = await h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "a:m", ["a", "b", "c", "d", "e"]), _ct);

        Assert.Equal([2, 2, 1], a.Requests.Select(r => r.Inputs.Count));
        Assert.Equal([["a", "b"], ["c", "d"], ["e"]], a.Requests.Select(r => r.Inputs.ToArray()));
        Assert.Equal(5, result.Vectors.Count);
        Assert.Equal([1f, 2f, 1f, 2f, 1f], result.Vectors.Select(v => v[0]));
        Assert.Equal(3, h.Usage.Records.Count);
        Assert.Equal(5, result.InputTokens);
    }

    [Fact]
    public async Task The_default_cap_is_2048_inputs()
    {
        var (h, a) = Setup();
        a.Enqueue(Vectors(2048));
        a.Enqueue(Vectors(2));

        var result = await h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "a:m", [.. Enumerable.Range(0, 2050).Select(i => "t" + i)]), _ct);

        Assert.Equal(2050, result.Vectors.Count);
        Assert.Equal([2048, 2], a.Requests.Select(r => r.Inputs.Count));
    }

    [Fact]
    public async Task A_mid_batch_failure_returns_no_partial_results()
    {
        var (h, a) = Setup(maxBatch: 1);
        a.Enqueue(Vectors(1));
        a.Enqueue(new EmbeddingOutcome.Failed(new ModelEvent.Failure("invalid_request", "bad")));

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() =>
            h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "a:m", ["a", "b", "c"]), _ct));

        Assert.Equal("invalid_request", ex.Code);
        Assert.Equal(2, a.Requests.Count);
        Assert.Equal([UsageStatus.Success, UsageStatus.Error], h.Usage.Records.Select(r => r.Status));
    }

    [Fact]
    public async Task A_retryable_mid_batch_failure_restarts_the_whole_batch_on_the_next_candidate()
    {
        var (h, a) = Setup(maxBatch: 1);
        h.EmbeddingModel("b", maxBatch: 5);
        var b = new ScriptedEmbeddingAdapter();
        h.Adapters["b"] = b;
        a.Enqueue(Vectors(1));
        a.Enqueue(new EmbeddingOutcome.Failed(new ModelEvent.Failure("rate_limited", "slow")));
        b.Enqueue(Vectors(2));

        var result = await h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "r", ["a", "b"]), _ct);

        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal(["a", "b"], Assert.Single(b.Requests).Inputs);
    }

    [Fact]
    public async Task A_too_long_input_is_rejected_before_any_call()
    {
        var (h, a) = Setup(maxChars: 5);

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() =>
            h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "a:m", ["ok", "123456"]), _ct));

        Assert.Equal("embedding_input_too_long", ex.Code);
        Assert.Empty(a.Requests);
        Assert.Empty(h.Usage.Records);
    }

    [Fact]
    public async Task An_input_only_some_candidates_refuse_goes_to_a_candidate_that_accepts_it()
    {
        var (h, a) = Setup(maxChars: 3);
        h.EmbeddingModel("b", maxInputChars: 100);
        var b = new ScriptedEmbeddingAdapter();
        h.Adapters["b"] = b;
        b.Enqueue(Vectors(1));

        var result = await h.Gateway().EmbedAsync(new EmbeddingRequest(GatewayHarness.Agent, "r", ["longer input"]), _ct);

        Assert.Single(result.Vectors);
        Assert.Empty(a.Requests);
    }

    // --- migration ---

    [Fact]
    public void The_gateway_migration_is_embedded_and_planned()
    {
        var files = new Bazaar.Server.Data.Migrations.EmbeddedMigrationSource(typeof(UsageRepository).Assembly);

        Assert.Contains("gateway", files.Namespaces());
        var file = Assert.Single(files.Load("gateway"));
        Assert.Contains("ADD COLUMN kind", file.Sql, StringComparison.Ordinal);
        Assert.Contains("embed_max_batch", file.Sql, StringComparison.Ordinal);
        Assert.Contains("embed_max_input_chars", file.Sql, StringComparison.Ordinal);
    }
}
