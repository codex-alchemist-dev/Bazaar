using Bazaar.Kodo.Journal;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Kodo.Postgres;

/// <summary>
/// The Kodo journal in PostgreSQL (migrations <c>kodo/0001_journal.sql</c> and <c>kodo/0002_state_history_and_snapshot.sql</c>, doc 46
/// sections 7.5 and 7.10). An append runs in one transaction under an advisory lock, so two writers (the active and a standby, or a
/// restart overlapping a slow shutdown) cannot interleave. Which records to write is decided by <see cref="KodoJournalAppendPlanner"/>,
/// so replaying the local spill twice stores every record once.
/// </summary>
public sealed class PgKodoJournalStore(NpgsqlDataSource dataSource) : IKodoJournalStore, IKodoCheckpointStore
{
    private const string _columns = "sequence, at_ticks, kind, payload, previous_hash, record_hash";

    /// <summary>Key of the transaction-scoped advisory lock that serialises appends.</summary>
    internal const long AppendLockKey = 0x4B4F444F_4A524E4CL;

    /// <summary>Key of the advisory lock that serialises checkpoint writes.</summary>
    internal const long CheckpointLockKey = 0x4B4F444F_43484B50L;

    internal const string LockSql = "SELECT pg_advisory_xact_lock($1)";
    internal const string HeadSql = "SELECT " + _columns + " FROM kodo_journal ORDER BY sequence DESC LIMIT 1";
    internal const string StoredHashesSql = "SELECT sequence, record_hash FROM kodo_journal WHERE sequence BETWEEN $1 AND $2";

    /// <summary>Batch insert. A hash that is already stored is skipped by the unique key, which keeps replay idempotent.</summary>
    internal const string InsertSql =
        "INSERT INTO kodo_journal (" + _columns + ") " +
        "SELECT * FROM unnest($1::bigint[], $2::bigint[], $3::text[], $4::bytea[], $5::bytea[], $6::bytea[]) " +
        "ON CONFLICT (record_hash) DO NOTHING";

    internal const string InsertStateChangeSql =
        "INSERT INTO kodo_state_change (sequence, at_ticks, from_state, to_state) " +
        "SELECT * FROM unnest($1::bigint[], $2::bigint[], $3::text[], $4::text[]) ON CONFLICT (sequence) DO NOTHING";

    internal const string ReadRangeSql = "SELECT " + _columns + " FROM kodo_journal WHERE sequence >= $1 ORDER BY sequence LIMIT $2";
    internal const string HashesSql = "SELECT sequence, record_hash FROM kodo_journal ORDER BY sequence LIMIT $1";

    internal const string PruneJournalSql =
        "DELETE FROM kodo_journal WHERE at_ticks < $1 AND sequence < (SELECT max(sequence) FROM kodo_journal)";

    /// <summary>Retention bounded by <see cref="KodoPruneRule"/>: $1 is the age cutoff, $2 the highest sequence that may go.</summary>
    internal const string PruneAnchoredJournalSql = "DELETE FROM kodo_journal WHERE at_ticks < $1 AND sequence <= $2";

    internal const string HeadSequenceSql = "SELECT max(sequence) FROM kodo_journal";

    internal const string NewestAnchoredSql =
        "SELECT max(c.sequence) FROM kodo_checkpoint c WHERE EXISTS (SELECT 1 FROM kodo_checkpoint_anchor a WHERE a.checkpoint_sequence = c.sequence)";

    internal const string PruneStateSql = "DELETE FROM kodo_state_change WHERE at_ticks < $1";

    internal const string SaveSnapshotSql =
        "INSERT INTO kodo_snapshot (id, revision, body, saved_at) VALUES (1, $1, $2, $3) " +
        "ON CONFLICT (id) DO UPDATE SET revision = EXCLUDED.revision, body = EXCLUDED.body, saved_at = EXCLUDED.saved_at";

    internal const string GetSnapshotSql = "SELECT revision, body, saved_at FROM kodo_snapshot WHERE id = 1";

    internal const string RecentStateChangesSql =
        "SELECT sequence, at_ticks, from_state, to_state FROM kodo_state_change ORDER BY sequence DESC LIMIT $1";

    internal const string StateChangePageSql =
        "SELECT sequence, at_ticks, from_state, to_state FROM kodo_state_change WHERE ($1::bigint IS NULL OR sequence < $1) ORDER BY sequence DESC LIMIT $2";

    /// <inheritdoc />
    public async ValueTask<JournalRecord?> GetHeadAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = dataSource.CreateCommand(HeadSql);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<KodoJournalAppendResult> AppendAsync(IReadOnlyList<JournalRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return default;
        }

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand lockCommand = new(LockSql, connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter<long> { TypedValue = AppendLockKey });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        JournalRecord? head;
        await using (NpgsqlCommand headCommand = new(HeadSql, connection, transaction))
        await using (NpgsqlDataReader reader = await headCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            head = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
        }

        Dictionary<long, byte[]> stored = [];
        await using (NpgsqlCommand hashesCommand = new(StoredHashesSql, connection, transaction))
        {
            hashesCommand.Parameters.Add(new NpgsqlParameter<long> { TypedValue = records[0].Sequence });
            hashesCommand.Parameters.Add(new NpgsqlParameter<long> { TypedValue = records[^1].Sequence });
            await using NpgsqlDataReader reader = await hashesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                stored[reader.GetInt64(0)] = reader.GetFieldValue<byte[]>(1);
            }
        }

        KodoJournalAppendPlan plan = KodoJournalAppendPlanner.Plan(head, stored, records);
        if (plan.Conflict is { } conflict)
        {
            throw new KodoJournalConflictException(conflict, plan.Message ?? "The journal records conflict with the stored chain.");
        }

        if (plan.ToInsert.Count > 0)
        {
            await using NpgsqlCommand insert = new(InsertSql, connection, transaction);
            AddRecordArrays(insert, plan.ToInsert);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            KodoStateChange[] changes = [.. plan.ToInsert.Select(KodoStateChangeParser.TryParse).OfType<KodoStateChange>()];
            if (changes.Length > 0)
            {
                await using NpgsqlCommand stateInsert = new(InsertStateChangeSql, connection, transaction);
                AddStateChangeArrays(stateInsert, changes);
                await stateInsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new KodoJournalAppendResult(plan.ToInsert.Count, plan.Skipped);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<JournalRecord>> ReadRangeAsync(long fromSequence, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using NpgsqlCommand cmd = dataSource.CreateCommand(ReadRangeSql);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = fromSequence });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = limit });
        List<JournalRecord> records = [];
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(ReadRecord(reader));
            }
        }

        if (KodoJournalRangeVerifier.FirstBroken(records) is int broken)
        {
            throw new KodoJournalConflictException(KodoJournalConflict.BrokenChain, $"Stored journal record {records[broken].Sequence} does not verify.");
        }

        return records;
    }

    /// <inheritdoc />
    public async ValueTask<KodoJournalProofView?> ProofAsync(long sequence, CancellationToken cancellationToken)
    {
        if (sequence < 0)
        {
            return null;
        }

        await using NpgsqlCommand cmd = dataSource.CreateCommand(HashesSql);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = KodoJournalProofs.MaxLeaves + 1L });
        List<byte[]> hashes = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The history must start at record 0 and have no hole, or a root over it would not be the journal's root.
            if (reader.GetInt64(0) != hashes.Count)
            {
                return null;
            }

            hashes.Add(reader.GetFieldValue<byte[]>(1));
        }

        return hashes.Count > KodoJournalProofs.MaxLeaves ? null : KodoJournalProofs.Build(sequence, hashes);
    }

    /// <inheritdoc />
    public async ValueTask<long> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        long cutoff = olderThan.ToUniversalTime().UtcTicks;
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long deleted;
        await using (NpgsqlCommand journal = new(PruneJournalSql, connection, transaction))
        {
            journal.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cutoff });
            deleted = await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand state = new(PruneStateSql, connection, transaction))
        {
            state.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cutoff });
            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <inheritdoc />
    public async ValueTask SaveSnapshotAsync(long revision, byte[] snapshotJson, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        await using NpgsqlCommand cmd = dataSource.CreateCommand(SaveSnapshotSql);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = revision });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = snapshotJson });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = savedAt.ToUniversalTime() });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<KodoStoredSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = dataSource.CreateCommand(GetSnapshotSql);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new KodoStoredSnapshot(reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<DateTimeOffset>(2))
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KodoStateChange>> RecentStateChangesAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using NpgsqlCommand cmd = dataSource.CreateCommand(RecentStateChangesSql);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = limit });
        List<KodoStateChange> changes = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new KodoStateChange(reader.GetInt64(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2), reader.GetString(3)));
        }

        return changes;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KodoStateChange>> ReadStateChangesAsync(long? beforeSequence, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using NpgsqlCommand cmd = dataSource.CreateCommand(StateChangePageSql);
        AddStateChangePageParameters(cmd, beforeSequence, limit);
        List<KodoStateChange> changes = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new KodoStateChange(reader.GetInt64(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2), reader.GetString(3)));
        }

        return changes;
    }

    /// <summary>Adds the two parameters of <see cref="StateChangePageSql"/>: the exclusive upper sequence (null for none) and the limit.</summary>
    internal static void AddStateChangePageParameters(NpgsqlCommand cmd, long? beforeSequence, int limit)
    {
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beforeSequence is long before ? before : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = limit });
    }

    private const string _checkpointColumns =
        "sequence, first_sequence, first_at_ticks, last_at_ticks, cut_at_ticks, previous_head, head_hash, root, digest, signature, signer_id";

    internal const string InsertCheckpointSql =
        "INSERT INTO kodo_checkpoint (" + _checkpointColumns + ") VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) ON CONFLICT (sequence) DO NOTHING";

    internal const string GetCheckpointSql = "SELECT " + _checkpointColumns + " FROM kodo_checkpoint WHERE sequence = $1";

    /// <summary>The window that overlaps $1..$2, for the overlap check before an insert.</summary>
    internal const string OverlapCheckpointSql = "SELECT 1 FROM kodo_checkpoint WHERE sequence >= $1 AND first_sequence <= $2 AND sequence <> $2 LIMIT 1";

    internal const string LatestCheckpointSql = "SELECT " + _checkpointColumns + " FROM kodo_checkpoint ORDER BY sequence DESC LIMIT 1";

    internal const string FirstStoredSequenceSql = "SELECT min(sequence) FROM kodo_journal";

    internal const string UnanchoredCheckpointsSql =
        "SELECT " + _checkpointColumns + " FROM kodo_checkpoint c " +
        "WHERE NOT EXISTS (SELECT 1 FROM kodo_checkpoint_anchor a WHERE a.checkpoint_sequence = c.sequence) ORDER BY sequence LIMIT $1";

    internal const string RecentCheckpointsSql = "SELECT " + _checkpointColumns + " FROM kodo_checkpoint ORDER BY sequence DESC LIMIT $1";

    internal const string AnchorsForSql =
        "SELECT checkpoint_sequence, sink_id, anchored_at_ticks FROM kodo_checkpoint_anchor WHERE checkpoint_sequence = ANY($1) ORDER BY checkpoint_sequence, sink_id";

    internal const string InsertAnchorSql =
        "INSERT INTO kodo_checkpoint_anchor (checkpoint_sequence, sink_id, anchored_at_ticks) VALUES ($1, $2, $3) ON CONFLICT (checkpoint_sequence, sink_id) DO NOTHING";

    /// <summary>The first checkpoint at or after $1 whose window starts at or before it: the window that covers the record.</summary>
    internal const string CoveringCheckpointSql =
        "SELECT " + _checkpointColumns + " FROM kodo_checkpoint WHERE sequence >= $1 AND first_sequence <= $1 ORDER BY sequence LIMIT 1";

    internal const string WindowHashesSql = "SELECT sequence, record_hash FROM kodo_journal WHERE sequence BETWEEN $1 AND $2 ORDER BY sequence";

    /// <inheritdoc />
    public async ValueTask<bool> SaveCheckpointAsync(KodoCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!KodoCheckpoints.DigestIsValid(checkpoint))
        {
            throw new KodoJournalConflictException(KodoJournalConflict.BrokenChain, "The checkpoint digest does not match its fields.");
        }

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (NpgsqlCommand lockCommand = new(LockSql, connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter<long> { TypedValue = CheckpointLockKey });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        KodoCheckpoint? existing;
        await using (NpgsqlCommand get = new(GetCheckpointSql, connection, transaction))
        {
            get.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.LastSequence });
            await using NpgsqlDataReader reader = await get.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            existing = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCheckpoint(reader) : null;
        }

        if (existing is not null)
        {
            return existing.Digest.AsSpan().SequenceEqual(checkpoint.Digest)
                ? false
                : throw new KodoJournalConflictException(KodoJournalConflict.Diverged, $"A different checkpoint is stored for sequence {checkpoint.LastSequence}.");
        }

        await using (NpgsqlCommand overlap = new(OverlapCheckpointSql, connection, transaction))
        {
            overlap.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.FirstSequence });
            overlap.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.LastSequence });
            if (await overlap.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new KodoJournalConflictException(KodoJournalConflict.Diverged, "The checkpoint window overlaps a stored checkpoint.");
            }
        }

        await using (NpgsqlCommand insert = new(InsertCheckpointSql, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.LastSequence });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.FirstSequence });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.FirstAt.UtcTicks });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.LastAt.UtcTicks });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.CutAt.UtcTicks });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = checkpoint.PreviousHead });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = checkpoint.HeadHash });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = checkpoint.Root });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = checkpoint.Digest });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = (object?)checkpoint.Signature ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)checkpoint.SignerId ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<KodoCheckpoint?> GetLatestCheckpointAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = dataSource.CreateCommand(LatestCheckpointSql);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCheckpoint(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<long?> GetFirstStoredSequenceAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = dataSource.CreateCommand(FirstStoredSequenceSql);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long first ? first : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KodoCheckpoint>> ListUnanchoredAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return await ReadCheckpointsAsync(UnanchoredCheckpointsSql, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KodoCheckpointEntry>> ListCheckpointsAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        IReadOnlyList<KodoCheckpoint> checkpoints = await ReadCheckpointsAsync(RecentCheckpointsSql, limit, cancellationToken).ConfigureAwait(false);
        if (checkpoints.Count == 0)
        {
            return [];
        }

        Dictionary<long, List<KodoAnchorMark>> marks = [];
        await using NpgsqlCommand cmd = dataSource.CreateCommand(AnchorsForSql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = checkpoints.Select(c => c.LastSequence).ToArray() });
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long key = reader.GetInt64(0);
                if (!marks.TryGetValue(key, out List<KodoAnchorMark>? list))
                {
                    marks[key] = list = [];
                }

                list.Add(new KodoAnchorMark(reader.GetString(1), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero)));
            }
        }

        return [.. checkpoints.Select(c => new KodoCheckpointEntry(c, marks.TryGetValue(c.LastSequence, out List<KodoAnchorMark>? list) ? list : []))];
    }

    /// <inheritdoc />
    public async ValueTask RecordAnchorAsync(long checkpointSequence, string sinkId, DateTimeOffset anchoredAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        await using NpgsqlCommand cmd = dataSource.CreateCommand(InsertAnchorSql);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpointSequence });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = sinkId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = anchoredAt.ToUniversalTime().UtcTicks });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<KodoCheckpointProof> ProofFromCheckpointAsync(long sequence, CancellationToken cancellationToken)
    {
        if (sequence < 0)
        {
            return new KodoCheckpointProof(KodoCheckpointProofOutcome.NotCheckpointed);
        }

        KodoCheckpoint? checkpoint;
        await using (NpgsqlCommand cmd = dataSource.CreateCommand(CoveringCheckpointSql))
        {
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = sequence });
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            checkpoint = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCheckpoint(reader) : null;
        }

        if (checkpoint is null)
        {
            return new KodoCheckpointProof(KodoCheckpointProofOutcome.NotCheckpointed);
        }

        List<KeyValuePair<long, byte[]>> hashes = [];
        await using (NpgsqlCommand cmd = dataSource.CreateCommand(WindowHashesSql))
        {
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.FirstSequence });
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = checkpoint.LastSequence });
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hashes.Add(new KeyValuePair<long, byte[]>(reader.GetInt64(0), reader.GetFieldValue<byte[]>(1)));
            }
        }

        return KodoCheckpoints.BuildProof(sequence, checkpoint, hashes);
    }

    /// <inheritdoc />
    public async ValueTask<long> PruneAnchoredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        long cutoff = olderThan.ToUniversalTime().UtcTicks;
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long? head, anchored;
        await using (NpgsqlCommand headCommand = new(HeadSequenceSql, connection, transaction))
        {
            head = await headCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        }

        await using (NpgsqlCommand anchoredCommand = new(NewestAnchoredSql, connection, transaction))
        {
            anchored = await anchoredCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        }

        long deleted = 0;
        long highest = head is { } h ? KodoPruneRule.HighestPrunable(h, anchored) : -1;
        if (highest >= 0)
        {
            await using NpgsqlCommand journal = new(PruneAnchoredJournalSql, connection, transaction);
            journal.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cutoff });
            journal.Parameters.Add(new NpgsqlParameter<long> { TypedValue = highest });
            deleted = await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand state = new(PruneStateSql, connection, transaction))
        {
            state.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cutoff });
            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private async Task<IReadOnlyList<KodoCheckpoint>> ReadCheckpointsAsync(string sql, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = limit });
        List<KodoCheckpoint> list = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadCheckpoint(reader));
        }

        return list;
    }

    private static KodoCheckpoint ReadCheckpoint(NpgsqlDataReader r) => new()
    {
        LastSequence = r.GetInt64(0),
        FirstSequence = r.GetInt64(1),
        FirstAt = new DateTimeOffset(r.GetInt64(2), TimeSpan.Zero),
        LastAt = new DateTimeOffset(r.GetInt64(3), TimeSpan.Zero),
        CutAt = new DateTimeOffset(r.GetInt64(4), TimeSpan.Zero),
        PreviousHead = r.GetFieldValue<byte[]>(5),
        HeadHash = r.GetFieldValue<byte[]>(6),
        Root = r.GetFieldValue<byte[]>(7),
        Digest = r.GetFieldValue<byte[]>(8),
        Signature = r.IsDBNull(9) ? null : r.GetFieldValue<byte[]>(9),
        SignerId = r.IsDBNull(10) ? null : r.GetString(10),
    };

    /// <summary>Adds the six array parameters of <see cref="InsertSql"/>, in column order.</summary>
    internal static void AddRecordArrays(NpgsqlCommand cmd, IReadOnlyList<JournalRecord> records)
    {
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = records.Select(r => r.Sequence).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = records.Select(r => r.At.UtcTicks).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = records.Select(r => r.Kind).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = records.Select(r => r.Payload).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = records.Select(r => r.PreviousHash).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = records.Select(r => r.Hash).ToArray() });
    }

    /// <summary>Adds the four array parameters of <see cref="InsertStateChangeSql"/>, in column order.</summary>
    internal static void AddStateChangeArrays(NpgsqlCommand cmd, IReadOnlyList<KodoStateChange> changes)
    {
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = changes.Select(c => c.Sequence).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = changes.Select(c => c.At.UtcTicks).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = changes.Select(c => c.From).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = changes.Select(c => c.To).ToArray() });
    }

    private static JournalRecord ReadRecord(NpgsqlDataReader r) => new()
    {
        Sequence = r.GetInt64(0),
        At = new DateTimeOffset(r.GetInt64(1), TimeSpan.Zero),
        Kind = r.GetString(2),
        Payload = r.GetFieldValue<byte[]>(3),
        PreviousHash = r.GetFieldValue<byte[]>(4),
        Hash = r.GetFieldValue<byte[]>(5),
    };
}
