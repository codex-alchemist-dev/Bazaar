using Bazaar.Contracts.Engines;
using Bazaar.Engines.Grants;
using Npgsql;

namespace Bazaar.Engines.Tests.Grants;

/// <summary>Real-Postgres tests for <c>engine_grants</c>. Skipped unless <c>BAZAAR_TEST_PG</c> is set.</summary>
public class EngineGrantStoreTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static EngineGrant Grant(Guid agent, RiskClass risk, string tool = "execute_luau", string by = "owner") =>
        new(agent, "roblox", tool, risk, by, _now);

    [Fact]
    public async Task Write_with_no_row_is_denied()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);

        Assert.False(await store.CheckAsync(Guid.NewGuid(), "roblox", "script_write", RiskClass.Write, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exec_with_no_row_is_denied()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);

        Assert.False(await store.CheckAsync(Guid.NewGuid(), "roblox", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Read_with_no_row_is_allowed()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);

        Assert.True(await store.CheckAsync(Guid.NewGuid(), "roblox", "script_read", RiskClass.Read, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Issued_grant_allows_its_own_agent_adapter_tool_and_risk_class()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(Grant(agent, RiskClass.Write, "script_write"), ct);

        Assert.True(await store.CheckAsync(agent, "roblox", "script_write", RiskClass.Write, ct));
    }

    [Fact]
    public async Task Grant_does_not_leak_to_another_agent_adapter_tool_or_risk_class()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(Grant(agent, RiskClass.Write, "script_write"), ct);

        Assert.False(await store.CheckAsync(Guid.NewGuid(), "roblox", "script_write", RiskClass.Write, ct));
        Assert.False(await store.CheckAsync(agent, "godot", "script_write", RiskClass.Write, ct));
        Assert.False(await store.CheckAsync(agent, "roblox", "execute_luau", RiskClass.Write, ct));
        // A write grant does not authorise an exec call on the same tool.
        Assert.False(await store.CheckAsync(agent, "roblox", "script_write", RiskClass.Exec, ct));
    }

    [Fact]
    public async Task Reissuing_a_grant_replaces_its_risk_class_and_granter()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(Grant(agent, RiskClass.Write, "execute_luau", by: "first"), ct);
        await store.IssueAsync(Grant(agent, RiskClass.Exec, "execute_luau", by: "second"), ct);

        Assert.False(await store.CheckAsync(agent, "roblox", "execute_luau", RiskClass.Write, ct));
        Assert.True(await store.CheckAsync(agent, "roblox", "execute_luau", RiskClass.Exec, ct));

        await using var cmd = db.DataSource.CreateCommand("SELECT count(*), max(granted_by) FROM engine_grants");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("second", reader.GetString(1));
    }

    [Fact]
    public async Task Check_constraint_rejects_an_unknown_risk_class()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO engine_grants (agent_id, adapter_id, tool_name, risk_class, granted_by) VALUES ($1, 'roblox', 't', 'admin', 'owner')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));
    }

    [Fact]
    public async Task Engine_projects_table_exists_after_migration()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);

        await using var cmd = db.DataSource.CreateCommand("SELECT to_regclass('engine_projects') IS NOT NULL");
        Assert.True((bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }
}
