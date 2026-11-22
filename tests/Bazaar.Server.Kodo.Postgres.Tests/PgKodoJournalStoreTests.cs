using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Journal;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Kodo.Postgres.Tests;

/// <summary>
/// The store against PostgreSQL. The server comes from <c>BAZAAR_TEST_PG</c>, or a Testcontainers container when it is unset
/// (<see cref="PgSource"/>); these tests need Docker or that variable. Each test gets its own database migrated with the kodo namespace only.
/// </summary>
public class PgKodoJournalStoreTests
{
    private static async Task<PgTestDatabase> OpenAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        return await PgTestDatabase.CreateAsync("kodo", MigrateAsync, ct);
    }

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        await new MigrationRunner(dataSource).MigrateAsync(
            new EmbeddedMigrationSource(typeof(PgKodoJournalStore).Assembly), MigrateCommand.CurrentProvider(), ct);

    [Fact]
    public async Task Stored_records_read_back_unchanged_and_verified_with_their_exact_ticks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore store = new(db.DataSource);
        List<JournalRecord> records = Chains.Build(5);

        KodoJournalAppendResult result = await store.AppendAsync(records, ct);
        IReadOnlyList<JournalRecord> read = await store.ReadRangeAsync(0, 100, ct);

        Assert.Equal(new KodoJournalAppendResult(5, 0), result);
        Assert.Equal(records.Select(r => r.Hash), read.Select(r => r.Hash));
        Assert.Equal(records.Select(r => r.At), read.Select(r => r.At));
        Assert.Equal(records[4].Hash, (await store.GetHeadAsync(ct))!.Hash);
        Assert.Null(JournalChain.Verify(read).FirstBrokenIndex);
    }

    [Fact]
    public async Task Replaying_the_same_spill_twice_stores_each_record_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore store = new(db.DataSource);
        List<JournalRecord> records = Chains.Build(6);
        await store.AppendAsync(records.GetRange(0, 3), ct);

        KodoJournalAppendResult overlap = await store.AppendAsync(records, ct);
        KodoJournalAppendResult again = await store.AppendAsync(records, ct);

        Assert.Equal(new KodoJournalAppendResult(3, 3), overlap);
        Assert.Equal(new KodoJournalAppendResult(0, 6), again);
        Assert.Equal(6, (await store.ReadRangeAsync(0, 100, ct)).Count);
    }

    [Fact]
    public async Task A_fork_or_a_hole_is_refused_and_leaves_the_store_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore store = new(db.DataSource);
        List<JournalRecord> mine = Chains.Build(4);
        await store.AppendAsync(mine, ct);

        var fork = await Assert.ThrowsAsync<KodoJournalConflictException>(async () =>
            await store.AppendAsync(Chains.Build(3, summary: i => $"other {i}"), ct));
        var hole = await Assert.ThrowsAsync<KodoJournalConflictException>(async () =>
            await store.AppendAsync(Chains.Build(2, firstSequence: 9, previous: Chains.Build(1, 8)[0].Hash), ct));

        Assert.Equal(KodoJournalConflict.Diverged, fork.Conflict);
        Assert.Equal(KodoJournalConflict.Gap, hole.Conflict);
        Assert.Equal(4, (await store.ReadRangeAsync(0, 100, ct)).Count);
    }

    [Fact]
    public async Task Two_writers_racing_with_the_same_records_store_them_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore a = new(db.DataSource);
        PgKodoJournalStore b = new(db.DataSource);
        List<JournalRecord> records = Chains.Build(20);

        KodoJournalAppendResult[] results = await Task.WhenAll(a.AppendAsync(records, ct).AsTask(), b.AppendAsync(records, ct).AsTask());

        Assert.Equal(20, results.Sum(r => r.Stored));
        Assert.Equal(20, (await a.ReadRangeAsync(0, 100, ct)).Count);
    }

    [Fact]
    public async Task A_tampered_row_fails_verification_on_read()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore store = new(db.DataSource);
        await store.AppendAsync(Chains.Build(4), ct);
        await using (var cmd = db.DataSource.CreateCommand("ALTER TABLE kodo_journal DISABLE TRIGGER kodo_journal_append_only"))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = db.DataSource.CreateCommand("UPDATE kodo_journal SET payload = '\\x00'::bytea WHERE sequence = 2"))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var error = await Assert.ThrowsAsync<KodoJournalConflictException>(async () => await store.ReadRangeAsync(0, 100, ct));

        Assert.Equal(KodoJournalConflict.BrokenChain, error.Conflict);
    }

    [Fact]
    public async Task Rows_cannot_be_updated_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        await new PgKodoJournalStore(db.DataSource).AppendAsync(Chains.Build(2), ct);
        await using var cmd = db.DataSource.CreateCommand("UPDATE kodo_journal SET payload = '\\x00'::bytea WHERE sequence = 1");

        await Assert.ThrowsAsync<PostgresException>(async () => await cmd.ExecuteNonQueryAsync(ct));
    }

    [Fact]
    public async Task State_records_write_the_state_history_in_the_same_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore store = new(db.DataSource);
        List<JournalRecord> first = Chains.Build(1, kind: "state", summary: _ => "state healthy -> degraded");
        List<JournalRecord> second = Chains.Build(1, firstSequence: 1, previous: first[0].Hash, kind: "state", summary: _ => "state degraded -> failing");

        await store.AppendAsync([.. first, .. second], ct);
        await store.AppendAsync(first, ct);
        IReadOnlyList<KodoStateChange> changes = await store.RecentStateChangesAsync(10, ct);

        Assert.Equal(["failing", "degraded"], changes.Select(c => c.To));
        Assert.Equal(first[0].At, changes[1].At);
    }

    [Fact]
    public async Task The_state_history_pages_newest_first_below_a_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore store = new(db.DataSource);
        List<JournalRecord> first = Chains.Build(1, kind: "state", summary: _ => "state healthy -> degraded");
        List<JournalRecord> second = Chains.Build(1, firstSequence: 1, previous: first[0].Hash, kind: "state", summary: _ => "state degraded -> failing");
        List<JournalRecord> third = Chains.Build(1, firstSequence: 2, previous: second[0].Hash, kind: "state", summary: _ => "state failing -> recovering");
        await store.AppendAsync([.. first, .. second, .. third], ct);

        IReadOnlyList<KodoStateChange> newest = await store.ReadStateChangesAsync(null, 2, ct);
        IReadOnlyList<KodoStateChange> older = await store.ReadStateChangesAsync(newest[^1].Sequence, 2, ct);

        Assert.Equal(["recovering", "failing"], newest.Select(c => c.To));
        Assert.Equal(["degraded"], older.Select(c => c.To));
        Assert.Empty(await store.ReadStateChangesAsync(0, 2, ct));
    }

    [Fact]
    public async Task The_snapshot_is_replaced_in_place_and_reads_back()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore store = new(db.DataSource);
        Assert.Null(await store.GetSnapshotAsync(ct));

        await store.SaveSnapshotAsync(3, [1, 2, 3], Chains.T0, ct);
        await store.SaveSnapshotAsync(1, [4, 5], Chains.T0.AddMinutes(1), ct);
        KodoStoredSnapshot? snapshot = await store.GetSnapshotAsync(ct);

        Assert.Equal(1, snapshot!.Revision);
        Assert.Equal(new byte[] { 4, 5 }, snapshot.Json);
    }

    [Fact]
    public async Task Retention_deletes_old_records_but_keeps_the_head()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore store = new(db.DataSource);
        await store.AppendAsync(Chains.Build(10), ct);

        long deleted = await store.PruneAsync(Chains.T0.AddDays(365), ct);
        IReadOnlyList<JournalRecord> left = await store.ReadRangeAsync(0, 100, ct);

        Assert.Equal(9, deleted);
        Assert.Equal(9L, Assert.Single(left).Sequence);
        Assert.Null(await store.ProofAsync(9, ct));
        Assert.Equal(new KodoJournalAppendResult(1, 0), await store.AppendAsync(Chains.Build(1, 10, left[0].Hash), ct));
    }

    [Fact]
    public async Task The_proof_covers_the_whole_history_and_verifies()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        PgKodoJournalStore store = new(db.DataSource);
        List<JournalRecord> records = Chains.Build(11);
        await store.AppendAsync(records, ct);

        KodoJournalProofView proof = (await store.ProofAsync(6, ct))!;
        MerkleInclusionProof inclusion = new(proof.Sequence, proof.TreeSize, [.. proof.Path.Select(Convert.FromHexString)]);

        Assert.Equal(11, proof.TreeSize);
        Assert.Equal(Convert.ToHexStringLower(records[6].Hash), proof.RecordHash);
        Assert.True(MerkleVerifier.VerifyInclusion(records[6].Hash, inclusion, Convert.FromHexString(proof.Root)));
        Assert.Null(await store.ProofAsync(11, ct));
    }

    private static async Task<(PgKodoJournalStore Store, List<JournalRecord> Records, KodoCheckpoint First, KodoCheckpoint Second)> SeedAsync(PgTestDatabase db, CancellationToken ct)
    {
        PgKodoJournalStore store = new(db.DataSource);
        List<JournalRecord> records = Chains.Build(10);
        await store.AppendAsync(records, ct);
        KodoCheckpoint first = KodoCheckpoints.Build(records.GetRange(0, 4), Chains.T0.AddHours(1))!;
        KodoCheckpoint second = KodoCheckpoints.Build(records.GetRange(4, 4), Chains.T0.AddHours(2))!;
        Assert.True(await store.SaveCheckpointAsync(first, ct));
        Assert.True(await store.SaveCheckpointAsync(second, ct));
        return (store, records, first, second);
    }

    [Fact]
    public async Task A_checkpoint_reads_back_unchanged_and_saving_it_again_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var (store, _, first, second) = await SeedAsync(db, ct);

        KodoCheckpoint latest = (await store.GetLatestCheckpointAsync(ct))!;

        Assert.Equal((second.FirstSequence, second.LastSequence, second.FirstAt, second.CutAt), (latest.FirstSequence, latest.LastSequence, latest.FirstAt, latest.CutAt));
        Assert.Equal(second.Digest, latest.Digest);
        Assert.True(KodoCheckpoints.DigestIsValid(latest));
        Assert.False(await store.SaveCheckpointAsync(first, ct));
        Assert.Equal(0L, await store.GetFirstStoredSequenceAsync(ct));
    }

    [Fact]
    public async Task A_different_checkpoint_for_the_same_end_or_an_overlapping_window_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var (store, records, first, _) = await SeedAsync(db, ct);
        KodoCheckpoint other = KodoCheckpoints.Build(records.GetRange(0, 4), Chains.T0.AddHours(9))!;
        KodoCheckpoint overlap = KodoCheckpoints.Build(records.GetRange(2, 4), Chains.T0.AddHours(9))!;

        Assert.NotEqual(first.Digest, other.Digest);
        await Assert.ThrowsAsync<KodoJournalConflictException>(async () => await store.SaveCheckpointAsync(other, ct));
        await Assert.ThrowsAsync<KodoJournalConflictException>(async () => await store.SaveCheckpointAsync(overlap, ct));
    }

    [Fact]
    public async Task Anchor_marks_move_a_checkpoint_out_of_the_unanchored_list_and_repeating_one_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var (store, _, first, second) = await SeedAsync(db, ct);

        Assert.Equal([first.LastSequence, second.LastSequence], (await store.ListUnanchoredAsync(10, ct)).Select(c => c.LastSequence));
        await store.RecordAnchorAsync(first.LastSequence, "file", Chains.T0.AddHours(3), ct);
        await store.RecordAnchorAsync(first.LastSequence, "file", Chains.T0.AddHours(4), ct);

        Assert.Equal([second.LastSequence], (await store.ListUnanchoredAsync(10, ct)).Select(c => c.LastSequence));
        IReadOnlyList<KodoCheckpointEntry> entries = await store.ListCheckpointsAsync(10, ct);
        Assert.Equal([second.LastSequence, first.LastSequence], entries.Select(e => e.Checkpoint.LastSequence));
        KodoAnchorMark mark = Assert.Single(entries[1].Anchors);
        Assert.Equal(("file", Chains.T0.AddHours(3)), (mark.SinkId, mark.AnchoredAt));
        Assert.Empty(entries[0].Anchors);
    }

    [Fact]
    public async Task The_proof_uses_the_covering_checkpoint_and_pruned_records_are_reported_not_hidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var (store, records, first, second) = await SeedAsync(db, ct);

        KodoCheckpointProof proof = await store.ProofFromCheckpointAsync(5, ct);
        Assert.Equal(KodoCheckpointProofOutcome.Found, proof.Outcome);
        Assert.Equal(Convert.ToHexStringLower(second.Root), proof.View!.Root);
        MerkleInclusionProof inclusion = new(5 - proof.View.WindowStart!.Value, proof.View.TreeSize, [.. proof.View.Path.Select(Convert.FromHexString)]);
        Assert.True(MerkleVerifier.VerifyInclusion(records[5].Hash, inclusion, second.Root));
        Assert.Equal(KodoCheckpointProofOutcome.NotCheckpointed, (await store.ProofFromCheckpointAsync(9, ct)).Outcome);

        await store.RecordAnchorAsync(first.LastSequence, "file", Chains.T0.AddHours(3), ct);
        await store.PruneAnchoredAsync(Chains.T0.AddDays(365), ct);

        Assert.Equal(KodoCheckpointProofOutcome.Pruned, (await store.ProofFromCheckpointAsync(2, ct)).Outcome);
        Assert.Equal(KodoCheckpointProofOutcome.Found, (await store.ProofFromCheckpointAsync(5, ct)).Outcome);
    }

    [Fact]
    public async Task Anchored_retention_keeps_the_records_after_the_newest_anchored_checkpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var (store, _, first, second) = await SeedAsync(db, ct);

        Assert.Equal(0, await store.PruneAnchoredAsync(Chains.T0.AddDays(365), ct));

        await store.RecordAnchorAsync(first.LastSequence, "file", Chains.T0.AddHours(3), ct);
        Assert.Equal(4, await store.PruneAnchoredAsync(Chains.T0.AddDays(365), ct));
        Assert.Equal(4L, (await store.ReadRangeAsync(0, 100, ct))[0].Sequence);

        await store.RecordAnchorAsync(second.LastSequence, "file", Chains.T0.AddHours(3), ct);
        Assert.Equal(4, await store.PruneAnchoredAsync(Chains.T0.AddDays(365), ct));
        Assert.Equal([8L, 9], (await store.ReadRangeAsync(0, 100, ct)).Select(r => r.Sequence));
        Assert.Equal(2, (await store.ListCheckpointsAsync(10, ct)).Count);
    }

    [Fact]
    public async Task A_stored_checkpoint_cannot_be_updated_or_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        await SeedAsync(db, ct);

        await using NpgsqlCommand update = db.DataSource.CreateCommand("UPDATE kodo_checkpoint SET cut_at_ticks = 1");
        await using NpgsqlCommand delete = db.DataSource.CreateCommand("DELETE FROM kodo_checkpoint");

        await Assert.ThrowsAsync<PostgresException>(async () => await update.ExecuteNonQueryAsync(ct));
        await Assert.ThrowsAsync<PostgresException>(async () => await delete.ExecuteNonQueryAsync(ct));
    }
}
