using Bazaar.Server.Agents.Turns.Tools.Db;
using Bazaar.Server.Tests.Security.Approvals.Db;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Agents.Turns.Tools.Db;

/// <summary>PostgresInFlightCallStore against a real PostgreSQL: begin, complete, and a lapsed lease that stays unfinished.</summary>
public sealed class PostgresInFlightCallStoreTests
{
    private const string _callKey = "0196a6f0-0000-7000-8000-000000000501:call-1";

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_key_is_not_unfinished()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresInFlightCallStore(db.DataSource);

        Assert.False(await store.HasUnfinishedAsync(_callKey, Ct));
    }

    [Fact]
    public async Task Begun_call_is_unfinished_until_it_completes()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresInFlightCallStore(db.DataSource);

        await store.BeginAsync(_callKey, Ct);
        Assert.True(await store.HasUnfinishedAsync(_callKey, Ct));

        await store.CompleteAsync(_callKey, Ct);
        Assert.False(await store.HasUnfinishedAsync(_callKey, Ct));
    }

    [Fact]
    public async Task Begun_call_with_a_lapsed_lease_is_still_unfinished()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresInFlightCallStore(db.DataSource);
        await store.BeginAsync(_callKey, Ct);

        // Simulate a crash long after the lease: the row stays begun, and the call may have run.
        await using (var cmd = db.DataSource.CreateCommand(
            "UPDATE in_flight_call SET lease_expires_at = now() - interval '1 hour' WHERE call_key = $1"))
        {
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = _callKey });
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync(Ct));
        }

        Assert.True(await store.HasUnfinishedAsync(_callKey, Ct));
    }

    [Fact]
    public async Task Begin_again_after_completion_reopens_the_call()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresInFlightCallStore(db.DataSource);
        await store.BeginAsync(_callKey, Ct);
        await store.CompleteAsync(_callKey, Ct);

        await store.BeginAsync(_callKey, Ct);

        Assert.True(await store.HasUnfinishedAsync(_callKey, Ct));
    }
}
