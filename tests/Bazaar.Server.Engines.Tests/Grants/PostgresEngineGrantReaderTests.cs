using Bazaar.Contracts.Engines;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Engines.Grants;
using Npgsql;

namespace Bazaar.Server.Engines.Tests.Grants;

/// <summary>Real-Postgres tests for the dispatcher's grant reader. Skipped unless <c>BAZAAR_TEST_PG</c> is set.</summary>
public class PostgresEngineGrantReaderTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Blank_grant_id_fails_closed_without_touching_the_database()
    {
        // The data source never connects: the reader must refuse before it queries.
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1");
        var reader = new PostgresEngineGrantReader(new EngineGrantStore(dataSource));

        Assert.False(await reader.CoversAsync(Guid.NewGuid(), "roblox", "script_write", RiskClass.Write, "", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Issued_grant_is_covered_for_its_agent_adapter_tool_and_risk_class()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(new EngineGrant(agent, "roblox", "script_write", RiskClass.Write, "owner", _now), ct);
        var reader = new PostgresEngineGrantReader(store);

        Assert.True(await reader.CoversAsync(agent, "roblox", "script_write", RiskClass.Write, "grant-1", ct));
        Assert.False(await reader.CoversAsync(Guid.NewGuid(), "roblox", "script_write", RiskClass.Write, "grant-1", ct));
        Assert.False(await reader.CoversAsync(agent, "roblox", "script_write", RiskClass.Exec, "grant-1", ct));
    }

    [Fact]
    public async Task Missing_row_and_blank_grant_id_are_denied()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.");
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(new EngineGrant(agent, "roblox", "script_write", RiskClass.Write, "owner", _now), ct);
        var reader = new PostgresEngineGrantReader(store);

        Assert.False(await reader.CoversAsync(agent, "roblox", "script_write", RiskClass.Write, " ", ct));
        Assert.False(await reader.CoversAsync(agent, "roblox", "other_tool", RiskClass.Write, "grant-1", ct));
    }
}
