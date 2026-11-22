using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Tests.Gateway.Pipeline;

public class ModelGatewayTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static ModelEvent.Failure OnlyFailure(IEnumerable<ModelEvent> events) =>
        Assert.IsType<ModelEvent.Failure>(Assert.Single(events));

    [Fact]
    public async Task Happy_path_forwards_events_and_writes_one_success_row()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal(["TextDelta", "UsageReported", "Done"], events.Select(e => e.GetType().Name));
        var row = Assert.Single(h.Usage.Records);
        Assert.Equal(UsageStatus.Success, row.Status);
        Assert.Equal(("a", "m", GatewayHarness.SecretRef("a")), (row.ProviderId, row.ModelId, row.KeyRef));
        Assert.Equal(GatewayHarness.Agent, row.AgentId);
        Assert.Empty(h.Adapter("b").Provider.Calls);
    }

    [Fact]
    public async Task Endpoint_carries_the_upstream_model_the_secret_and_the_headers()
    {
        var h = new GatewayHarness();
        h.World.Providers["a"] = h.World.Providers["a"] with { HeadersJson = """{"X-Org":"acme"}""" };
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());

        await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        var endpoint = Assert.Single(h.Adapter("a").Endpoints);
        Assert.Equal("m", endpoint.ModelId);
        Assert.Equal(FakeSecrets.Sentinel, endpoint.ApiKey);
        Assert.Equal("acme", endpoint.ExtraHeaders!["X-Org"]);
    }

    [Fact]
    public async Task Usage_identity_comes_from_the_model_row()
    {
        var h = new GatewayHarness();
        h.World.Models[new ModelRef("a", "m")] = h.World.Models[new ModelRef("a", "m")] with { Identity = GatewayIdentity.Pooled };
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());

        await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal(GatewayIdentity.Pooled, Assert.Single(h.Usage.Records).Identity);
    }

    [Fact]
    public async Task Policy_denied_yields_one_failure_and_calls_nothing()
    {
        var h = new GatewayHarness();

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request("c:m"), _ct), _ct);

        Assert.Equal("policy_denied", OnlyFailure(events).Code);
        Assert.Empty(h.Usage.Records);
        Assert.All(h.Adapters.Values, a => Assert.Empty(((ScriptedAdapter)a).Provider.Calls));
    }

    [Fact]
    public async Task Rate_limit_before_content_falls_back_and_writes_two_rows()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.RateLimited(30));
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.DoesNotContain(events, e => e is ModelEvent.Failure);
        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Equal([UsageStatus.Error, UsageStatus.Success], h.Usage.Records.Select(r => r.Status));
        Assert.Equal(["a", "b"], h.Usage.Records.Select(r => r.ProviderId));
        Assert.Equal("rate_limited", h.Usage.Records[0].ErrorCode);
        var report = Assert.Single(h.Quota.Reports);
        Assert.Equal((GatewayHarness.KeyOf("a"), "claude", "rate_limited"), (report.Key, report.Class, report.Failure.Code));
    }

    [Fact]
    public async Task Spend_cap_before_content_reroutes()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.SpendCapped());
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Equal("spend_capped", h.Usage.Records[0].ErrorCode);
    }

    [Fact]
    public async Task Failure_after_content_is_forwarded_without_fallback()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.TextDelta("par")), new ScriptStep.ConnectionDropped());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.TextDelta>(events[0]);
        Assert.Equal("stream_dropped", Assert.IsType<ModelEvent.Failure>(events[^1]).Code);
        Assert.Empty(h.Adapter("b").Provider.Calls);
        Assert.Equal(UsageStatus.Error, Assert.Single(h.Usage.Records).Status);
    }

    [Fact]
    public async Task Malformed_tool_json_is_never_retried_on_another_model()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.MalformedToolJson("t1", "tool", "{oops"));

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("malformed_tool_json", Assert.IsType<ModelEvent.Failure>(events[^1]).Code);
        Assert.Empty(h.Adapter("b").Provider.Calls);
    }

    [Fact]
    public async Task All_candidates_failing_forwards_the_last_failure()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.RateLimited());
        h.Adapter("b").Provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Failure("provider_error", "boom")));

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        var failure = OnlyFailure(events);
        Assert.Equal(("provider_error", "boom"), (failure.Code, failure.Message));
        Assert.Equal(2, h.Usage.Records.Count);
    }

    [Fact]
    public async Task Held_usage_is_forwarded_once_before_done_and_dropped_for_a_retried_attempt()
    {
        var h = new GatewayHarness();
        var usage = new ScriptStep.Emit(new ModelEvent.UsageReported(new GatewayTokenUsage(1, 0, 0, 0, 1, 0)));
        h.Adapter("a").Provider.EnqueueCall(usage, new ScriptStep.Emit(new ModelEvent.Failure("provider_error", "x")));
        h.Adapter("b").Provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.Warning("w", "careful")),
            usage,
            new ScriptStep.Emit(new ModelEvent.TextDelta("hi")),
            new ScriptStep.Emit(new ModelEvent.UsageReported(new GatewayTokenUsage(2, 0, 0, 0, 2, 0))),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal(["Warning", "TextDelta", "UsageReported", "Done"], events.Select(e => e.GetType().Name));
        Assert.Equal(2, ((ModelEvent.UsageReported)events[2]).Usage.Input);
    }

    [Fact]
    public async Task Fallback_never_leaves_the_resolved_route()
    {
        var h = new GatewayHarness();
        h.World.AddRoute("r", ["a:m", "b:m", "c:m"]);
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.RateLimited());
        h.Adapter("b").Provider.EnqueueCall(new ScriptStep.RateLimited());
        h.Adapter("c").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("rate_limited", OnlyFailure(events).Code);
        Assert.Empty(h.Adapter("c").Provider.Calls);
        Assert.Equal(1, h.Adapter("c").Provider.RemainingScripts);
    }

    [Fact]
    public async Task Open_breaker_candidate_is_skipped()
    {
        var h = new GatewayHarness(breakerThreshold: 1);
        h.Health.Observe(GatewayHarness.KeyOf("a"), new ModelEvent.Failure("provider_error", "x"));
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Empty(h.Adapter("a").Provider.Calls);
        Assert.Equal("b", Assert.Single(h.Usage.Records).ProviderId);
    }

    [Fact]
    public async Task Breaker_opens_after_repeated_provider_errors_across_calls()
    {
        var h = new GatewayHarness(breakerThreshold: 2);
        var gateway = h.Gateway();
        for (var i = 0; i < 2; i++)
        {
            h.Adapter("a").Provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Failure("provider_error", "x")));
            h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());
            await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);
        }

        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());
        var events = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Equal(2, h.Adapter("a").Provider.Calls.Count);
        Assert.Equal(3, h.Adapter("b").Provider.Calls.Count);
    }

    [Fact]
    public async Task Cancelled_half_open_trial_is_released_and_recorded_aborted()
    {
        var h = new GatewayHarness(breakerThreshold: 1);
        h.World.AddRoute("r", ["a:m"]);
        var gateway = h.Gateway();
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Failure("provider_error", "x")));
        await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);
        h.Clock.Advance(TimeSpan.FromMinutes(1));

        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in gateway.CallAsync(GatewayHarness.Request(), cts.Token))
            {
                Assert.IsType<ModelEvent.TextDelta>(e);
                await cts.CancelAsync();
            }
        });

        Assert.Equal(UsageStatus.Aborted, h.Usage.Records[^1].Status);
        Assert.Equal(2, h.Usage.Records.Count);
        Assert.True(h.Health.TryAcquire(GatewayHarness.KeyOf("a")));
    }

    [Fact]
    public async Task Disposing_the_enumerator_early_records_aborted_usage()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());

        await foreach (var e in h.Gateway().CallAsync(GatewayHarness.Request(), _ct))
        {
            Assert.IsType<ModelEvent.TextDelta>(e);
            break;
        }

        Assert.Equal(UsageStatus.Aborted, Assert.Single(h.Usage.Records).Status);
        Assert.Equal(BreakerState.Closed, h.Health.GetState(GatewayHarness.KeyOf("a")).State);
    }

    [Fact]
    public async Task Quota_blocked_candidate_is_skipped()
    {
        var h = new GatewayHarness();
        h.Quota.Blocked[(GatewayHarness.KeyOf("a"), "claude")] = (QuotaState.RateLimited, null);
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Empty(h.Adapter("a").Provider.Calls);
        Assert.Empty(h.Sink.Events);
    }

    [Fact]
    public async Task All_blocked_yields_no_capacity_and_publishes_once_then_recovery()
    {
        var h = new GatewayHarness();
        var until = h.Clock.GetUtcNow().AddSeconds(60);
        h.Quota.Blocked[(GatewayHarness.KeyOf("a"), "claude")] = (QuotaState.RateLimited, until.AddSeconds(30));
        h.Quota.Blocked[(GatewayHarness.KeyOf("b"), "claude")] = (QuotaState.DailyExhausted, until);
        var gateway = h.Gateway();

        var first = OnlyFailure(await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct));
        var second = OnlyFailure(await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct));

        Assert.Equal("no_capacity", first.Code);
        Assert.Equal(60, first.RetryAfterSeconds);
        Assert.Equal("no_capacity", second.Code);
        Assert.Empty(h.Usage.Records);
        var published = Assert.IsType<AgentCapacityChanged>(Assert.Single(h.Sink.Events));
        Assert.Equal((GatewayHarness.Agent, false, until, QuotaState.DailyExhausted), (published.AgentId, published.Available, published.Until, published.Reason));

        h.Quota.Blocked.Clear();
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());
        await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);
        await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal(2, h.Sink.Events.Count);
        var restored = Assert.IsType<AgentCapacityChanged>(h.Sink.Events[1]);
        Assert.Equal((GatewayHarness.Agent, true), (restored.AgentId, restored.Available));
    }

    [Fact]
    public async Task Health_blocked_candidates_also_count_as_no_capacity()
    {
        var h = new GatewayHarness(breakerThreshold: 1);
        h.Health.Observe(GatewayHarness.KeyOf("a"), new ModelEvent.Failure("timeout", "x"));
        h.Health.Observe(GatewayHarness.KeyOf("b"), new ModelEvent.Failure("timeout", "x"));

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("no_capacity", OnlyFailure(events).Code);
        var published = Assert.IsType<AgentCapacityChanged>(Assert.Single(h.Sink.Events));
        Assert.Equal(QuotaState.Down, published.Reason);
    }

    [Fact]
    public async Task Config_problem_skip_does_not_publish_a_capacity_change()
    {
        var h = new GatewayHarness();
        h.World.Providers["a"] = h.World.Providers["a"] with { Adapter = "no_such_adapter" };
        h.Quota.Blocked[(GatewayHarness.KeyOf("b"), "claude")] = (QuotaState.RateLimited, null);

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("no_capacity", OnlyFailure(events).Code);
        Assert.Empty(h.Sink.Events);
    }

    [Fact]
    public async Task Missing_secret_skips_the_candidate_as_a_config_problem()
    {
        var h = new GatewayHarness();
        h.Secrets.Values[GatewayHarness.SecretRef("a")] = null;
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Empty(h.Adapter("a").Provider.Calls);
    }

    [Fact]
    public async Task Delegated_route_serving_an_unexpected_model_warns_and_suspends_the_route()
    {
        var h = new GatewayHarness();
        h.AddProvider("up");
        h.World.AddRoute("custom/x", ["up:profile"], ["expected-model"]);
        h.World.AddPolicy(GatewayHarness.Agent, ["custom/x", "up:expected-model"], "custom/x");
        h.Adapter("up").Provider.EnqueueCall(GatewayHarness.Reply(served: "rogue-model"));

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request("custom/x"), _ct), _ct);

        Assert.Equal(["TextDelta", "Warning", "UsageReported", "Done"], events.Select(e => e.GetType().Name));
        Assert.Equal("served_model_outside_expected_set", ((ModelEvent.Warning)events[1]).Code);
        var call = Assert.Single(h.Suspender.Calls);
        Assert.Equal("custom/x", call.RouteId);
        Assert.Equal(["rogue-model"], call.Violations);
    }

    [Fact]
    public async Task Delegated_route_serving_an_expected_model_is_left_alone()
    {
        var h = new GatewayHarness();
        h.AddProvider("up");
        h.World.AddRoute("custom/x", ["up:profile"], ["expected-model"]);
        h.World.AddPolicy(GatewayHarness.Agent, ["custom/x", "up:expected-model"], "custom/x");
        h.Adapter("up").Provider.EnqueueCall(GatewayHarness.Reply(served: "expected-model"));

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request("custom/x"), _ct), _ct);

        Assert.DoesNotContain(events, e => e is ModelEvent.Warning);
        Assert.Empty(h.Suspender.Calls);
    }

    [Theory]
    [InlineData("oauth_subscription")]
    [InlineData("claude_max_login")]
    [InlineData("")]
    public async Task Subscription_style_auth_kind_is_refused_even_if_it_appears_after_resolution(string authKind)
    {
        var h = new GatewayHarness();
        h.Directory.ProviderOverride = p => p.Id == "a" ? p with { AuthKind = authKind } : p;
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Empty(h.Adapter("a").Provider.Calls);
        Assert.Empty(h.Adapter("a").Endpoints);
    }

    [Fact]
    public async Task Subscription_style_auth_kind_stored_for_the_only_provider_is_denied_by_policy()
    {
        var h = new GatewayHarness();
        h.World.Providers["a"] = h.World.Providers["a"] with { AuthKind = "oauth_subscription" };
        h.World.AddRoute("r", ["a:m"]);

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("policy_denied", OnlyFailure(events).Code);
        Assert.Empty(h.Adapter("a").Provider.Calls);
    }

    [Fact]
    public async Task Secret_value_never_appears_in_events_usage_or_adapter_exceptions()
    {
        var h = new GatewayHarness();
        h.Adapters["a"] = new ThrowingAdapter($"upstream rejected key {FakeSecrets.Sentinel}");
        h.Adapter("b").Provider.EnqueueCall(new ScriptStep.RateLimited());
        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        var denied = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request("c:m"), _ct), _ct);

        var failure = OnlyFailure(events);
        Assert.Equal("rate_limited", failure.Code);
        var everything = string.Join('\n', events.Concat(denied).Select(e => e.ToString())
            .Concat(h.Usage.Records.Select(r => r.ToString()))
            .Concat(h.InternalErrors.Select(e => e.Message)));
        Assert.DoesNotContain(FakeSecrets.Sentinel, everything, StringComparison.Ordinal);
        Assert.Equal("provider_error", h.Usage.Records[0].ErrorCode);
    }

    [Fact]
    public async Task Usage_write_failure_does_not_break_the_callers_stream()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());
        var gateway = new ModelGateway(
            h.World.Resolver(),
            h.Directory,
            new DictionaryAdapterRegistry(h.Adapters),
            h.Secrets,
            h.Health,
            new Bazaar.Server.Gateway.Usage.UsageRecorder(new ThrowingStore()),
            onInternalError: h.InternalErrors.Add);

        var events = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Single(h.InternalErrors);
    }

    private sealed class ThrowingStore : Bazaar.Server.Gateway.Usage.IUsageStore
    {
        public ValueTask AppendAsync(GatewayUsageRecord record, CancellationToken ct) => throw new InvalidOperationException("disk full");
    }
}
