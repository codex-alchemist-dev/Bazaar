using Bazaar.Server.OutcomeLedger;
using Bazaar.Server.OutcomeLedger.Abstractions;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.OutcomeLedger.Tests;

/// <summary>A throwaway database with the two ledger tables, columns as in common/0013.</summary>
public sealed class LedgerDb : IAsyncLifetime
{
    private PgSource? _source;
    private string _database = "";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _source = await PgSource.GetSharedAsync();
        _database = "outcome_ledger_" + Guid.NewGuid().ToString("N");
        await using (var admin = NpgsqlDataSource.Create(_source.AdminConnectionString))
        {
            await using var create = admin.CreateCommand($"CREATE DATABASE \"{_database}\"");
            await create.ExecuteNonQueryAsync();
        }

        DataSource = NpgsqlDataSource.Create(_source.ConnectionStringFor(_database));
        await using var tables = DataSource.CreateCommand("""
            CREATE TABLE outcome_event (
                id uuid PRIMARY KEY, kind text NOT NULL CHECK (kind IN ('ci_first_run','revert','post_merge_defect','owner_accept','owner_reject','manual')),
                pr_ref text NOT NULL, x numeric NOT NULL CHECK (x BETWEEN 0 AND 1), w numeric NOT NULL, dedupe_key text NOT NULL UNIQUE,
                source_ref text, occurred_at timestamptz NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE outcome_attribution (
                outcome_id uuid NOT NULL REFERENCES outcome_event (id), agent_id text NOT NULL, role text NOT NULL,
                tag text NOT NULL, w numeric NOT NULL, PRIMARY KEY (outcome_id, agent_id, tag));
            """);
        await tables.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(_source!.AdminConnectionString);
        await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(string table)
    {
        await using var cmd = DataSource.CreateCommand($"SELECT count(*) FROM {table}");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

public class PgOutcomeLedgerStoreTests(LedgerDb db) : IClassFixture<LedgerDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static OutcomeRecord Record(string key) => new(
        OutcomeKind.Revert, "org/repo#7", 0m, key, _t0,
        [new OutcomeAttribution("a1", "author", "csharp", 1m), new OutcomeAttribution("a2", "approver", "csharp", 0.2m)],
        1m, "src");

    [Fact]
    public async Task Appends_the_event_and_every_attribution()
    {
        var store = new PgOutcomeLedgerStore(db.DataSource);
        var before = await db.CountAsync("outcome_attribution");

        var id = await store.AppendAsync(Record("gh:pg-1"), _t0, TestContext.Current.CancellationToken);

        Assert.NotNull(id);
        Assert.Equal(before + 2, await db.CountAsync("outcome_attribution"));
        await using var cmd = db.DataSource.CreateCommand("SELECT kind, x, w, source_ref FROM outcome_event WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id!.Value);
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await r.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("revert", r.GetString(0));
        Assert.Equal(0m, r.GetDecimal(1));
        Assert.Equal("src", r.GetString(3));
    }

    [Fact]
    public async Task A_replay_adds_nothing()
    {
        var store = new PgOutcomeLedgerStore(db.DataSource);
        await store.AppendAsync(Record("gh:pg-2"), _t0, TestContext.Current.CancellationToken);
        var events = await db.CountAsync("outcome_event");
        var rows = await db.CountAsync("outcome_attribution");

        var again = await store.AppendAsync(Record("gh:pg-2"), _t0, TestContext.Current.CancellationToken);

        Assert.Null(again);
        Assert.Equal(events, await db.CountAsync("outcome_event"));
        Assert.Equal(rows, await db.CountAsync("outcome_attribution"));
    }

    [Fact]
    public async Task Concurrent_appends_for_one_key_record_one_row()
    {
        var store = new PgOutcomeLedgerStore(db.DataSource);
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.AppendAsync(Record("gh:pg-3"), _t0, TestContext.Current.CancellationToken)));

        Assert.Single(results, r => r is not null);
    }
}
