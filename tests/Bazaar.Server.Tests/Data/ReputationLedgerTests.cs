using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Tests.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Data;

/// <summary>Migration <c>common/0013_reputation_ledger.sql</c> on real PostgreSQL (doc 11, WP1).</summary>
public sealed class ReputationLedgerTests(MigrationDb db) : IClassFixture<MigrationDb>
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(MigrationRunner).Assembly);

    private static readonly string[] _tables =
        ["review", "outcome_event", "outcome_attribution", "reviewer_calibration", "weights_version", "score_snapshot", "reputation_event"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<NpgsqlDataSource> MigratedAsync()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using (var reset = db.DataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public"))
            await reset.ExecuteNonQueryAsync(Ct);
        await new MigrationRunner(db.DataSource, new MigrationOptions(16)).MigrateAsync(_source, "linux", Ct);
        return db.DataSource;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> SqlStateAsync(NpgsqlConnection conn, string sql) =>
        (await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, sql))).SqlState;

    private const string _insertOutcome =
        "INSERT INTO outcome_event (kind, pr_ref, x, w, dedupe_key, occurred_at) " +
        "VALUES ('ci_first_run', 'pr-1', 0.5, 1.0, 'ci:pr-1', now())";

    [Fact]
    public async Task All_seven_ledger_tables_exist()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = ANY(@names)", conn);
        cmd.Parameters.AddWithValue("names", _tables);

        Assert.Equal(7L, Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Outcome_events_are_append_only()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, _insertOutcome);

        Assert.Equal("42501", await SqlStateAsync(conn, "UPDATE outcome_event SET x = 0.9"));
        Assert.Equal("42501", await SqlStateAsync(conn, "DELETE FROM outcome_event"));
    }

    [Fact]
    public async Task Reputation_events_cannot_be_truncated()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, "INSERT INTO reputation_event (agent_id, tag, kind, actor, at) VALUES ('bob', 'csharp', 'promote', 'owner', now())");

        Assert.Equal("42501", await SqlStateAsync(conn, "TRUNCATE reputation_event"));
    }

    [Fact]
    public async Task Duplicate_dedupe_key_is_a_unique_violation()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, _insertOutcome);

        Assert.Equal("23505", await SqlStateAsync(conn, _insertOutcome));
    }

    [Fact]
    public async Task Outcome_x_outside_zero_to_one_is_rejected()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);

        Assert.Equal("23514", await SqlStateAsync(conn,
            "INSERT INTO outcome_event (kind, pr_ref, x, w, dedupe_key, occurred_at) " +
            "VALUES ('revert', 'pr-2', 1.5, 1.0, 'revert:pr-2', now())"));
    }

    [Fact]
    public async Task Review_overall_must_be_between_one_and_five()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);

        Assert.Equal("23514", await SqlStateAsync(conn,
            "INSERT INTO review (pr_ref, reviewer_agent_id, author_agent_id, blinded, tag, overall) " +
            "VALUES ('pr-3', 'alice', 'bob', true, 'csharp', 6)"));
    }

    [Fact]
    public async Task Score_snapshot_needs_an_existing_weights_version()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);

        Assert.Equal("23503", await SqlStateAsync(conn,
            "INSERT INTO score_snapshot (agent_id, tag, s, lcb, n_eff, weights_version) " +
            "VALUES ('bob', 'csharp', 0.7, 0.6, 12, 99)"));
    }
}
