using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.TimeMachine.Tests;

public class PostgresReplayMarkerSourceTests
{
    private static readonly DateTimeOffset _t0 = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public async Task Reads_only_time_and_kind_in_range_and_in_order()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("tmaudit", MigrateAsync, ct);
        await InsertAsync(db.DataSource, 1, _t0, "setting.changed", ct);
        await InsertAsync(db.DataSource, 2, _t0.AddMinutes(1), "approval.granted", ct);
        await InsertAsync(db.DataSource, 3, _t0.AddMinutes(2), "kill_switch.fired", ct);
        var source = new PostgresReplayMarkerSource(db.DataSource);

        var all = await source.ListAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 100, ct);
        var window = await source.ListAsync(_t0.AddSeconds(30), _t0.AddMinutes(1), 100, ct);
        var capped = await source.ListAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 2, ct);

        Assert.Equal(["setting.changed", "approval.granted", "kill_switch.fired"], all.Select(m => m.Label));
        Assert.Equal(["approval.granted"], window.Select(m => m.Label));
        Assert.Equal(2, capped.Count);
    }

    [Fact]
    public async Task A_database_without_the_audit_table_has_no_markers()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("tmnoaudit", (_, _) => Task.CompletedTask, ct);

        var found = await new PostgresReplayMarkerSource(db.DataSource).ListAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 10, ct);

        Assert.Empty(found);
    }

    // The columns the source reads; the real table (doc 15) has more, which this source never touches.
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "CREATE TABLE audit_log (seq bigint PRIMARY KEY, ts timestamptz NOT NULL, actor text NOT NULL, kind text NOT NULL, payload jsonb NOT NULL)");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertAsync(NpgsqlDataSource dataSource, long seq, DateTimeOffset ts, string kind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("INSERT INTO audit_log (seq, ts, actor, kind, payload) VALUES ($1, $2, 'secret-actor', $3, '{\"secret\":1}')");
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = ts });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
