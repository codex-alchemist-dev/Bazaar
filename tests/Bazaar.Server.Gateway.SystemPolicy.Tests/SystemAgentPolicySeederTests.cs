using System.Text.Json;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Gateway.SystemPolicy.Tests;

public sealed class SystemAgentPolicySeederTests
{
    private const string Agent = EmbeddingsPolicySource.EmbeddingsAgentId;

    [Fact]
    public async Task Creates_a_missing_row_for_a_model_id()
    {
        var h = new Harness("openai:text-embedding-3-small");

        var written = await h.Seeder.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, written);
        var row = h.Store.Rows[Agent];
        Assert.Equal(["openai:text-embedding-3-small"], row.Allowed);
        Assert.Equal("openai:text-embedding-3-small", row.DefaultRoute);
        Assert.Equal(SystemAgentPolicySeeder.ChangedBy, row.ChangedBy);
    }

    [Fact]
    public async Task Route_name_expands_to_member_models_and_the_route_is_default()
    {
        var h = new Harness("embed-main");
        h.Directory.Routes["embed-main"] = new GwRoute(
            "embed-main",
            RouteStrategy.Priority,
            [ModelRef.Parse("openai:text-embedding-3-small"), ModelRef.Parse("voyage:voyage-3")],
            []);

        await h.Seeder.ReconcileAsync(TestContext.Current.CancellationToken);

        var row = h.Store.Rows[Agent];
        Assert.Equal(["embed-main", "openai:text-embedding-3-small", "voyage:voyage-3"], row.Allowed);
        Assert.Equal("embed-main", row.DefaultRoute);
    }

    [Fact]
    public async Task Refreshes_a_system_seed_row_when_the_route_changes()
    {
        var h = new Harness("openai:old-model");
        await h.Seeder.ReconcileAsync(TestContext.Current.CancellationToken);

        h.Settings.Value = "openai:new-model";
        var written = await h.Seeder.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, written);
        Assert.Equal(["openai:new-model"], h.Store.Rows[Agent].Allowed);
        Assert.Equal(2, h.Store.Saves);
    }

    [Fact]
    public async Task Unchanged_declaration_does_not_write_again()
    {
        var h = new Harness("openai:m");
        await h.Seeder.ReconcileAsync(TestContext.Current.CancellationToken);

        var written = await h.Seeder.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, written);
        Assert.Equal(1, h.Store.Saves);
    }

    [Fact]
    public async Task Leaves_an_operator_edited_row_alone()
    {
        var h = new Harness("openai:new-model");
        h.Store.Rows[Agent] = new AgentModelPolicyRow(Agent, ["openai:operator-pick"], "openai:operator-pick", 3, "operator:alice", DateTimeOffset.UnixEpoch);

        var written = await h.Seeder.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, written);
        Assert.Equal(0, h.Store.Saves);
        Assert.Equal(["openai:operator-pick"], h.Store.Rows[Agent].Allowed);
        Assert.Equal("operator:alice", h.Store.Rows[Agent].ChangedBy);
    }

    [Fact]
    public async Task Empty_setting_writes_nothing()
    {
        var h = new Harness("");

        var written = await h.Seeder.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, written);
        Assert.Empty(h.Store.Rows);
    }

    [Fact]
    public async Task Unknown_route_writes_nothing()
    {
        var h = new Harness("no-such-route");

        var written = await h.Seeder.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, written);
        Assert.Empty(h.Store.Rows);
    }

    [Fact]
    public async Task Start_reconciles()
    {
        var h = new Harness("openai:m");

        await h.Seeder.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(h.Store.Rows.ContainsKey(Agent));
    }

    private sealed class Harness
    {
        public Harness(string setting)
        {
            Settings = new FakeSettings { Value = setting };
            Source = new EmbeddingsPolicySource(Settings, Directory, NullLogger<EmbeddingsPolicySource>.Instance);
            Seeder = new SystemAgentPolicySeeder([Source], Store, NullLogger<SystemAgentPolicySeeder>.Instance);
        }

        public FakeSettings Settings { get; }

        public FakeDirectory Directory { get; } = new();

        public FakeStore Store { get; } = new();

        public EmbeddingsPolicySource Source { get; }

        public SystemAgentPolicySeeder Seeder { get; }
    }

    private sealed class FakeStore : ISystemPolicyStore
    {
        public Dictionary<string, AgentModelPolicyRow> Rows { get; } = [];

        public int Saves { get; private set; }

        public Task<AgentModelPolicyRow?> GetRowAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.GetValueOrDefault(agentId));

        public Task SaveAsync(string agentId, IReadOnlyList<string> entries, string? defaultRoute, string changedBy, CancellationToken cancellationToken)
        {
            Saves++;
            var version = Rows.TryGetValue(agentId, out var old) ? old.Version + 1 : 1;
            Rows[agentId] = new AgentModelPolicyRow(agentId, entries, defaultRoute, version, changedBy, DateTimeOffset.UnixEpoch);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDirectory : IGatewayDirectory
    {
        public Dictionary<string, GwRoute> Routes { get; } = [];

        public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) => ValueTask.FromResult<GwProvider?>(null);

        public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) => ValueTask.FromResult<GwModel?>(null);

        public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Routes.GetValueOrDefault(routeId));
    }

    private sealed class FakeSettings : ISettingStore
    {
        public string Value { get; set; } = "";

        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default)
        {
            Assert.Equal(GatewaySettings.EmbeddingRoute.Name, key.Name);
            return Task.FromResult((T)(object)Value);
        }

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
