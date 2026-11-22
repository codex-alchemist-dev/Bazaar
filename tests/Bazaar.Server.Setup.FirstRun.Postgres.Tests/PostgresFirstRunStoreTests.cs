namespace Bazaar.Server.Setup.FirstRun.Postgres.Tests;

/// <summary>The seeded GC and agent rows: idempotence, membership, the agent's state and policy, and rollback. Needs PostgreSQL.</summary>
public sealed class PostgresFirstRunStoreTests
{
    private static readonly string[] _whitelist = ["claude-first-agent", "anthropic-direct:claude-haiku-5-5"];

    [Fact]
    public async Task The_general_gc_is_made_once_with_the_owner_as_a_member()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        var owner = await FirstRunDatabase.AddOwnerAsync(db.DataSource, ct);
        var store = new PostgresFirstRunStore(db.DataSource);

        var first = await store.EnsureGeneralGcAsync("General", owner, ct);
        var second = await store.EnsureGeneralGcAsync("General", owner, ct);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.GcId, second.GcId);
        Assert.Equal(1, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM gc", ct));
        Assert.Equal("general", await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT type_key FROM gc", ct));
        Assert.Equal(owner, await FirstRunDatabase.ScalarAsync<Guid>(db.DataSource, $"SELECT principal_id FROM gc_member WHERE gc_id = '{first.GcId}'", ct));
    }

    [Fact]
    public async Task The_agent_is_created_unactivated_with_exactly_the_whitelist_and_joins_the_gc()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        var owner = await FirstRunDatabase.AddOwnerAsync(db.DataSource, ct);
        var store = new PostgresFirstRunStore(db.DataSource);
        var gc = await store.EnsureGeneralGcAsync("General", owner, ct);

        var agent = await store.EnsureDefaultAgentAsync("Claude", "Helps", gc.GcId, _whitelist, ct);
        var again = await store.EnsureDefaultAgentAsync("Other name", "x", gc.GcId, [], ct);

        Assert.True(agent.Created);
        Assert.False(again.Created);
        Assert.Equal(agent.AgentId, again.AgentId);
        Assert.Equal("created", await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT state FROM agent", ct));
        Assert.Equal("Claude", await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT name FROM agent", ct));
        Assert.Equal("[\"claude-first-agent\", \"anthropic-direct:claude-haiku-5-5\"]", await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT allowed::text FROM agent_model_policy", ct));
        Assert.Equal("claude-first-agent", await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT default_route FROM agent_model_policy", ct));
        Assert.Equal(1, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, $"SELECT count(*) FROM gc_member WHERE principal_id = '{agent.AgentId}'", ct));
    }

    [Fact]
    public async Task An_empty_whitelist_writes_no_policy_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        var store = new PostgresFirstRunStore(db.DataSource);

        await store.EnsureDefaultAgentAsync("Claude", "", null, [], ct);

        Assert.Equal(0, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM agent_model_policy", ct));
    }

    [Fact]
    public async Task Activation_moves_only_a_created_agent_to_active()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        var store = new PostgresFirstRunStore(db.DataSource);
        var agent = await store.EnsureDefaultAgentAsync("Claude", "", null, _whitelist, ct);

        await store.ActivateAgentAsync(agent.AgentId, ct);

        Assert.Equal("active", await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT state FROM agent", ct));
    }

    [Fact]
    public async Task Rollback_removes_the_agent_and_archives_the_gc_so_a_later_seed_starts_fresh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        var owner = await FirstRunDatabase.AddOwnerAsync(db.DataSource, ct);
        var store = new PostgresFirstRunStore(db.DataSource);
        var gc = await store.EnsureGeneralGcAsync("General", owner, ct);
        var agent = await store.EnsureDefaultAgentAsync("Claude", "", gc.GcId, _whitelist, ct);

        await store.RemoveAgentAsync(agent.AgentId, ct);
        await store.ArchiveGcAsync(gc.GcId, ct);

        Assert.Equal(0, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM agent", ct));
        Assert.Equal(0, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM agent_model_policy", ct));
        Assert.Equal(1, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM gc WHERE archived_at IS NOT NULL", ct));
        var next = await store.EnsureGeneralGcAsync("General", owner, ct);
        Assert.True(next.Created);
        Assert.NotEqual(gc.GcId, next.GcId);
    }
}
