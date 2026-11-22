using Bazaar.Contracts.Gateway;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Gateway.Data;

public sealed class GatewayDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
        {
            return;
        }

        await base.InitializeAsync();
        var source = new EmbeddedMigrationSource(typeof(AssemblyMarker).Assembly);
        await new MigrationRunner(DataSource).MigrateAsync(source, "linux");
    }
}

/// <summary>Real-Postgres CRUD for the six gateway tables (migration common/0002).</summary>
public sealed class GatewayDataTests(GatewayDb db) : IClassFixture<GatewayDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 25, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static GwProvider Provider(string id, string authKind = "api_key", string? secretRef = "secret://bazaar/providers/x/key", bool enabled = true) =>
        new(id, "first_party", "openai_chat", "https://example.invalid/v1", authKind, secretRef, "{}", null, null, enabled);

    private GatewayDirectoryRepository Directory() => new(db.DataSource);

    [Fact]
    public async Task Provider_round_trips_and_upsert_replaces()
    {
        SkipWithoutDatabase();
        var repo = Directory();
        await repo.UpsertProviderAsync(Provider("p-roundtrip") with { TermsAckAt = _t0, HeadersJson = """{"x-a":"b"}""" }, Ct);
        var read = await repo.GetProviderAsync("p-roundtrip", Ct);
        Assert.NotNull(read);
        Assert.Equal(_t0, read.TermsAckAt);
        Assert.Contains("x-a", read.HeadersJson, StringComparison.Ordinal);

        await repo.UpsertProviderAsync(Provider("p-roundtrip", enabled: false), Ct);
        Assert.False((await repo.GetProviderAsync("p-roundtrip", Ct))!.Enabled);
        Assert.Null(await repo.GetProviderAsync("nope", Ct));
        Assert.Contains(await repo.ListProvidersAsync(Ct), p => p.Id == "p-roundtrip");
    }

    [Theory]
    [InlineData("subscription", "secret://bazaar/providers/x/key")]
    [InlineData("oauth", "secret://bazaar/providers/x/key")]
    [InlineData("api_key", "sk-literal-key")]
    [InlineData("api_key", null)]
    [InlineData("none", "secret://bazaar/providers/x/key")]
    public async Task Database_refuses_subscription_kinds_and_literal_keys(string authKind, string? secretRef)
    {
        SkipWithoutDatabase();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Directory().UpsertProviderAsync(Provider("p-bad", authKind, secretRef), Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Auth_none_is_stored_without_a_secret()
    {
        SkipWithoutDatabase();
        await Directory().UpsertProviderAsync(Provider("p-local", "none", null), Ct);
        Assert.Null((await Directory().GetProviderAsync("p-local", Ct))!.SecretRef);
    }

    [Fact]
    public async Task Model_upsert_keeps_first_seen_and_requires_a_provider()
    {
        SkipWithoutDatabase();
        var repo = Directory();
        await repo.UpsertProviderAsync(Provider("p-models"), Ct);
        var model = new GwModel("p-models", "m1", "claude", GatewayIdentity.Pooled, """{"tools":true}""", GwModel.StatusAvailable, _t0, _t0, "probe");
        await repo.UpsertModelAsync(model, Ct);
        await repo.UpsertModelAsync(model with { FirstSeen = _t0.AddDays(1), LastSeen = _t0.AddDays(1), Status = GwModel.StatusMissing }, Ct);

        var read = await repo.GetModelAsync(new ModelRef("p-models", "m1"), Ct);
        Assert.NotNull(read);
        Assert.Equal(_t0, read.FirstSeen);
        Assert.Equal(_t0.AddDays(1), read.LastSeen);
        Assert.Equal(GwModel.StatusMissing, read.Status);
        Assert.Equal(GatewayIdentity.Pooled, read.Identity);
        Assert.Single(await repo.ListModelsAsync("p-models", Ct));

        await Assert.ThrowsAsync<PostgresException>(() => repo.UpsertModelAsync(model with { ProviderId = "ghost" }, Ct));
    }

    [Fact]
    public async Task Route_round_trips_in_order_and_rejects_bad_names()
    {
        SkipWithoutDatabase();
        var repo = Directory();
        var route = new GwRoute("lane-rt", RouteStrategy.RoundRobin, [new ModelRef("b", "m2"), new ModelRef("a", "m1:tag")], ["m1"]);
        await repo.UpsertRouteAsync(route, Ct);
        var read = await repo.GetRouteAsync("lane-rt", Ct);
        Assert.NotNull(read);
        Assert.Equal(RouteStrategy.RoundRobin, read.Strategy);
        Assert.Equal(route.Members, read.Members);
        Assert.Equal(["m1"], read.ExpectedSet);
        Assert.Null(await repo.GetRouteAsync("nope", Ct));

        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertRouteAsync(route with { Id = "auto/best" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertRouteAsync(route with { Id = "custom/*" }, Ct));
    }

    [Fact]
    public async Task Policy_save_validates_and_versions()
    {
        SkipWithoutDatabase();
        var repo = new AgentModelPolicyRepository(db.DataSource, new FixedClock(_t0));
        Assert.Null(await repo.GetAsync("agent-new", Ct));

        Assert.Equal(1, await repo.SaveAsync("agent-new", ["p:m", "lane"], "lane", "owner", Ct));
        Assert.Equal(2, await repo.SaveAsync("agent-new", ["p:m"], null, "owner", Ct));
        var policy = await repo.GetAsync("agent-new", Ct);
        Assert.NotNull(policy);
        Assert.Equal(2, policy.Version);
        Assert.True(policy.AllowsModel(new ModelRef("p", "m")));
        Assert.False(policy.AllowsRoute("lane"));
        var row = await repo.GetRowAsync("agent-new", Ct);
        Assert.Equal("owner", row!.ChangedBy);
        Assert.Equal(_t0, row.ChangedAt);

        await Assert.ThrowsAsync<PolicyFormatException>(() => repo.SaveAsync("agent-new", ["auto/cheap"], null, "owner", Ct));
        await Assert.ThrowsAsync<PolicyFormatException>(() => repo.SaveAsync("agent-new", ["p:*"], null, "owner", Ct));
        Assert.Equal(2, (await repo.GetAsync("agent-new", Ct))!.Version);
    }

    [Fact]
    public async Task A_stored_policy_that_no_longer_parses_reads_as_no_policy()
    {
        SkipWithoutDatabase();
        await using (var cmd = db.DataSource.CreateCommand(
            "INSERT INTO agent_model_policy (agent_id, allowed, version, changed_by, changed_at) VALUES ('agent-tampered', '[\"auto/*\"]'::jsonb, 1, 'x', now())"))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var repo = new AgentModelPolicyRepository(db.DataSource);
        Assert.Null(await repo.GetAsync("agent-tampered", Ct));
        Assert.NotNull(await repo.GetRowAsync("agent-tampered", Ct));
    }

    [Fact]
    public async Task Resolver_runs_end_to_end_on_the_real_tables()
    {
        SkipWithoutDatabase();
        var dir = Directory();
        var policies = new AgentModelPolicyRepository(db.DataSource);
        await dir.UpsertProviderAsync(Provider("e2e-native"), Ct);
        await dir.UpsertProviderAsync(Provider("e2e-pool"), Ct);
        foreach (var (p, id) in new[] { ("e2e-native", GatewayIdentity.Native), ("e2e-pool", GatewayIdentity.Pooled) })
        {
            await dir.UpsertModelAsync(new GwModel(p, "haiku", "claude", id, "{}", GwModel.StatusAvailable, _t0, _t0, "test"), Ct);
        }

        await dir.UpsertRouteAsync(new GwRoute("e2e-lane", RouteStrategy.Priority, [new ModelRef("e2e-native", "haiku"), new ModelRef("e2e-pool", "haiku")], []), Ct);
        await policies.SaveAsync("agent-e2e", ["e2e-lane", "e2e-native:haiku"], "e2e-lane", "owner", Ct);

        var resolver = new RouteResolver(policies, dir);
        var request = new ModelRequest("agent-e2e", "e2e-lane", [new ModelMessage(ModelRole.User, "hi")], []);
        var allowed = Assert.IsType<RouteResolution.Allowed>(await resolver.ResolveAsync(request, Ct));
        Assert.Equal([new ModelRef("e2e-native", "haiku")], allowed.Route.Candidates.Select(c => c.Model));

        var ghost = request with { AgentId = "ghost" };
        Assert.Equal(DenyReason.UnknownAgent, Assert.IsType<RouteResolution.Denied>(await resolver.ResolveAsync(ghost, Ct)).Reason);
    }

    [Fact]
    public async Task Quota_state_upserts_per_key_and_class()
    {
        SkipWithoutDatabase();
        await Directory().UpsertProviderAsync(Provider("p-quota"), Ct);
        var repo = new QuotaStateRepository(db.DataSource);
        var state = new GwQuotaState("p-quota", "key-1", "claude", QuotaState.RateLimited, _t0.AddMinutes(5), """{"remaining":0}""");
        await repo.UpsertAsync(state, Ct);
        await repo.UpsertAsync(state with { State = QuotaState.SpendCapped, Until = null }, Ct);
        await repo.UpsertAsync(state with { KeyRef = "key-2", State = QuotaState.Available, Until = null }, Ct);

        var read = await repo.GetAsync("p-quota", "key-1", "claude", Ct);
        Assert.Equal(QuotaState.SpendCapped, read!.State);
        Assert.Null(read.Until);
        Assert.Equal(QuotaState.Available, (await repo.GetAsync("p-quota", "key-2", "claude", Ct))!.State);
        Assert.Null(await repo.GetAsync("p-quota", "key-3", "claude", Ct));
        Assert.Equal(2, (await repo.ListAsync(Ct)).Count(s => s.ProviderId == "p-quota"));
        await Assert.ThrowsAsync<PostgresException>(() => repo.UpsertAsync(state with { ProviderId = "ghost" }, Ct));
    }

    [Fact]
    public async Task Usage_round_trips_every_column_and_keeps_the_identity_label()
    {
        SkipWithoutDatabase();
        await Directory().UpsertProviderAsync(Provider("p-usage"), Ct);
        var repo = new UsageRepository(db.DataSource);
        var native = Usage("agent-u", GatewayIdentity.Native, _t0);
        var pooled = Usage("agent-u", GatewayIdentity.Pooled, _t0.AddMinutes(1)) with
        {
            Status = UsageStatus.Error,
            ErrorCode = "rate_limited",
            RetryAfterSeconds = 30,
            Estimated = true,
            TurnId = null,
            ServedModel = null,
            PriceVersion = null,
        };
        await repo.AppendAsync(native, Ct);
        await repo.AppendAsync(pooled, Ct);
        await repo.AppendAsync(Usage("agent-other", GatewayIdentity.Native, _t0), Ct);

        var rows = await repo.ListAsync("agent-u", _t0, _t0.AddHours(1), Ct);
        Assert.Equal([native, pooled], rows);
        Assert.Single(await repo.ListAsync("agent-u", _t0.AddSeconds(1), _t0.AddHours(1), Ct));
    }

    [Fact]
    public async Task Usage_keeps_input_separate_from_cache_reads_and_rejects_impossible_rows()
    {
        SkipWithoutDatabase();
        await Directory().UpsertProviderAsync(Provider("p-usage2"), Ct);
        var repo = new UsageRepository(db.DataSource);
        var row = Usage("agent-c", GatewayIdentity.Native, _t0, "p-usage2") with { Tokens = new GatewayTokenUsage(10, 0, 0, 5000, 20, 5) };
        await repo.AppendAsync(row, Ct);
        var read = Assert.Single(await repo.ListAsync("agent-c", _t0, _t0.AddHours(1), Ct));
        Assert.Equal(10, read.Tokens.Input);
        Assert.Equal(5000, read.Tokens.CacheRead);

        var tooMuchThinking = row with { Id = Guid.NewGuid(), Tokens = new GatewayTokenUsage(1, 0, 0, 0, 2, 3) };
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => repo.AppendAsync(tooMuchThinking, Ct).AsTask())).SqlState);
        var negative = row with { Id = Guid.NewGuid(), Tokens = new GatewayTokenUsage(-1, 0, 0, 0, 0, 0) };
        await Assert.ThrowsAsync<PostgresException>(() => repo.AppendAsync(negative, Ct).AsTask());
    }

    [Fact]
    public async Task Usage_recorder_writes_through_the_postgres_store()
    {
        SkipWithoutDatabase();
        await Directory().UpsertProviderAsync(Provider("p-rec"), Ct);
        var clock = new FixedClock(_t0);
        var acc = new UsageAccumulator("agent-rec", "turn-1", "p-rec", "key-1", "m", GatewayIdentity.Pooled, clock);
        acc.Observe(new ModelEvent.TextDelta("hello"));
        acc.Observe(new ModelEvent.UsageReported(new GatewayTokenUsage(7, 0, 0, 0, 3, 0)));
        acc.Observe(new ModelEvent.Done(StopReason.EndTurn, "m-served"));
        var store = new UsageRepository(db.DataSource);
        await new UsageRecorder(store).RecordAsync(acc, null, Ct);

        var row = Assert.Single(await store.ListAsync("agent-rec", _t0.AddHours(-1), _t0.AddHours(1), Ct));
        Assert.Equal(GatewayIdentity.Pooled, row.Identity);
        Assert.Equal(UsageStatus.Success, row.Status);
        Assert.Equal("m-served", row.ServedModel);
    }

    private static GatewayUsageRecord Usage(string agent, GatewayIdentity identity, DateTimeOffset at, string provider = "p-usage") => new(
        Guid.NewGuid(), at, agent, "turn-1", provider, "key-1", "claude-haiku-5-5", "claude-haiku-5-5-20260101", identity,
        new GatewayTokenUsage(100, 10, 20, 30, 40, 5), 250, UsageStatus.Success, null, null, false, "2026-10");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
