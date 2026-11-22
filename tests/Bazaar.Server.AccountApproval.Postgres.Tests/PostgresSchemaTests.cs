using Bazaar.Server.AccountApproval.Abstractions;
using Bazaar.Server.AccountApproval.Tests;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.AccountApproval.Postgres.Tests;

/// <summary>Database-level guarantees, checked with raw SQL that goes around the store.</summary>
public sealed class PostgresSchemaTests
{
    [Fact]
    public async Task The_database_itself_refuses_a_duplicate_vote()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ApprovalDatabase.CreateAsync(ct);
        await new PostgresAccountApprovalStore(db.DataSource).TryCreateAsync(Fixtures.Request(), new Pending("req-1", 2, 2), ct);
        const string insert =
            "INSERT INTO account_approval_vote (request_id, approver_agent_id, kind, model_id, reasoning_hash, cast_at) " +
            "VALUES ('req-1', 'agent-a', 'Approve', 'model-x', 'hash-1', now())";
        await Exec(db.DataSource, insert, ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(db.DataSource, insert, ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_final_decision()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ApprovalDatabase.CreateAsync(ct);
        await new PostgresAccountApprovalStore(db.DataSource).TryCreateAsync(Fixtures.Request(), new Denied("req-1", DecisionRule.UnverifiedTerms), ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            db.DataSource,
            "INSERT INTO account_approval_decision (request_id, outcome, rule) VALUES ('req-1', 'needs_owner', 'PeerRefused')",
            ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task The_runtime_role_can_read_and_insert_but_not_change_or_delete_history()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ApprovalDatabase.CreateAsync(ct);

        foreach (var table in new[] { "account_approval_request", "account_approval_vote", "account_approval_decision" })
        {
            Assert.True(await HasPrivilege(db.DataSource, table, "SELECT", ct));
            Assert.True(await HasPrivilege(db.DataSource, table, "INSERT", ct));
            Assert.False(await HasPrivilege(db.DataSource, table, "UPDATE", ct));
            Assert.False(await HasPrivilege(db.DataSource, table, "DELETE", ct));
        }
    }

    [Fact]
    public async Task A_service_over_a_store_that_cannot_read_a_stored_value_does_not_approve()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ApprovalDatabase.CreateAsync(ct);
        await new PostgresAccountApprovalStore(db.DataSource).TryCreateAsync(Fixtures.Request(), new Pending("req-1", 2, 2), ct);
        await Exec(db.DataSource, "UPDATE account_approval_request SET terms = 'Garbled'", ct);
        var h = new Harness(store: new PostgresAccountApprovalStore(db.DataSource));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.GetAsync("req-1", ct));
    }

    private static async Task Exec(NpgsqlDataSource dataSource, string sql, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HasPrivilege(NpgsqlDataSource dataSource, string table, string privilege, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT has_table_privilege('bazaar_app', $1, $2)");
        cmd.Parameters.AddWithValue(table);
        cmd.Parameters.AddWithValue(privilege);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
