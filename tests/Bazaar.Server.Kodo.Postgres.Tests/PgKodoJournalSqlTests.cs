using Bazaar.Kodo.Journal;
using Bazaar.Server.Data.Migrations;
using Npgsql;

namespace Bazaar.Server.Kodo.Postgres.Tests;

/// <summary>Checks that need no database: the statement text, the parameter shapes and the embedded migrations.</summary>
public class PgKodoJournalSqlTests
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(PgKodoJournalStore).Assembly);

    [Fact]
    public void The_insert_skips_a_stored_hash_so_replay_is_idempotent()
    {
        Assert.Contains("ON CONFLICT (record_hash) DO NOTHING", PgKodoJournalStore.InsertSql);
        Assert.Contains("kodo_journal_record_hash_key UNIQUE (record_hash)", _source.Load("kodo").Single(f => f.Version == 1).Sql);
    }

    [Fact]
    public void Appends_take_a_transaction_scoped_advisory_lock()
    {
        Assert.Contains("pg_advisory_xact_lock", PgKodoJournalStore.LockSql);
    }

    [Fact]
    public void Record_parameters_are_six_parallel_arrays_in_column_order()
    {
        List<JournalRecord> records = Chains.Build(3);
        using NpgsqlCommand cmd = new(PgKodoJournalStore.InsertSql);

        PgKodoJournalStore.AddRecordArrays(cmd, records);

        Assert.Equal(6, cmd.Parameters.Count);
        Assert.Equal(new long[] { 0, 1, 2 }, (long[])cmd.Parameters[0].Value!);
        Assert.Equal(records.Select(r => r.At.UtcTicks), (long[])cmd.Parameters[1].Value!);
        Assert.Equal(records.Select(r => r.Kind), (string[])cmd.Parameters[2].Value!);
        Assert.Equal(records.Select(r => r.Payload), (byte[][])cmd.Parameters[3].Value!);
        Assert.Equal(records.Select(r => r.PreviousHash), (byte[][])cmd.Parameters[4].Value!);
        Assert.Equal(records.Select(r => r.Hash), (byte[][])cmd.Parameters[5].Value!);
    }

    [Fact]
    public void The_history_page_is_newest_first_bounded_and_takes_an_optional_exclusive_upper_sequence()
    {
        Assert.Contains("ORDER BY sequence DESC LIMIT $2", PgKodoJournalStore.StateChangePageSql);
        Assert.Contains("sequence < $1", PgKodoJournalStore.StateChangePageSql);
        using NpgsqlCommand open = new(PgKodoJournalStore.StateChangePageSql);
        using NpgsqlCommand bounded = new(PgKodoJournalStore.StateChangePageSql);

        PgKodoJournalStore.AddStateChangePageParameters(open, null, 50);
        PgKodoJournalStore.AddStateChangePageParameters(bounded, 120, 10);

        Assert.Equal(2, open.Parameters.Count);
        Assert.Equal(DBNull.Value, open.Parameters[0].Value);
        Assert.Equal(120L, bounded.Parameters[0].Value);
        Assert.Equal(10L, ((NpgsqlParameter<long>)bounded.Parameters[1]).TypedValue);
    }

    [Fact]
    public void The_exact_ticks_are_stored_because_a_microsecond_column_would_break_the_hash()
    {
        List<JournalRecord> records = Chains.Build(1);

        Assert.NotEqual(0, records[0].At.UtcTicks % 10);
        Assert.Contains("at_ticks      bigint", _source.Load("kodo").Single(f => f.Version == 1).Sql);
    }

    [Fact]
    public void The_migrations_live_in_the_kodo_namespace_not_in_common()
    {
        Assert.Contains("kodo", _source.Namespaces());
        IReadOnlyList<MigrationFile> files = _source.Load("kodo");
        Assert.Equal(["journal", "state_history_and_snapshot", "checkpoints"], files.OrderBy(f => f.Version).Select(f => f.Name));
        Assert.Contains("CREATE TABLE kodo_journal (", files[0].Sql);
        Assert.Contains("CREATE TABLE kodo_state_change (", files[1].Sql);
        Assert.Contains("CREATE TABLE kodo_snapshot (", files[1].Sql);
        Assert.Contains("CREATE TABLE kodo_checkpoint (", files[2].Sql);
        Assert.Contains("CREATE TABLE kodo_checkpoint_anchor (", files[2].Sql);
        Assert.Empty(_source.Load(MigrationRunner.Common));
    }

    [Fact]
    public void Retention_never_deletes_the_head()
    {
        Assert.Contains("sequence < (SELECT max(sequence) FROM kodo_journal)", PgKodoJournalStore.PruneJournalSql);
    }

    [Fact]
    public void Checkpoint_rows_are_append_only_so_a_root_outlives_retention()
    {
        string sql = _source.Load("kodo").Single(f => f.Version == 3).Sql;

        Assert.Contains("BEFORE UPDATE OR DELETE ON kodo_checkpoint", sql);
        Assert.Contains("(signature IS NULL) = (signer_id IS NULL)", sql);
    }

    [Fact]
    public void Anchored_retention_is_bounded_by_the_prune_rule_and_never_touches_checkpoints()
    {
        Assert.Contains("sequence <= $2", PgKodoJournalStore.PruneAnchoredJournalSql);
        Assert.DoesNotContain("kodo_checkpoint", PgKodoJournalStore.PruneAnchoredJournalSql);
        Assert.Contains("EXISTS (SELECT 1 FROM kodo_checkpoint_anchor", PgKodoJournalStore.NewestAnchoredSql);
        Assert.Equal(-1, KodoPruneRule.HighestPrunable(10, null));
    }

    [Fact]
    public void The_covering_checkpoint_is_the_first_one_at_or_after_the_record()
    {
        Assert.Contains("sequence >= $1 AND first_sequence <= $1 ORDER BY sequence LIMIT 1", PgKodoJournalStore.CoveringCheckpointSql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM kodo_checkpoint_anchor", PgKodoJournalStore.UnanchoredCheckpointsSql);
        Assert.Contains("ON CONFLICT (checkpoint_sequence, sink_id) DO NOTHING", PgKodoJournalStore.InsertAnchorSql);
    }

    [Fact]
    public async Task A_snapshot_read_without_a_database_is_an_error_the_caller_can_catch()
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=unreachable.invalid;Timeout=1;Command Timeout=1");
        PgKodoJournalStore store = new(dataSource);

        await Assert.ThrowsAnyAsync<Exception>(async () => await store.GetHeadAsync(TestContext.Current.CancellationToken));
    }
}
