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
public sealed class PgKodoJournalStore(NpgsqlDataSource dataSource) : IKodoJournalStore
{
    private const string _columns = "sequence, at_ticks, kind, payload, previous_hash, record_hash";

    /// <summary>Key of the transaction-scoped advisory lock that serialises appends.</summary>
    internal const long AppendLockKey = 0x4B4F444F_4A524E4CL;

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

    internal const string PruneStateSql = "DELETE FROM kodo_state_change WHERE at_ticks < $1";

    internal const string SaveSnapshotSql =
        "INSERT INTO kodo_snapshot (id, revision, body, saved_at) VALUES (1, $1, $2, $3) " +
        "ON CONFLICT (id) DO UPDATE SET revision = EXCLUDED.revision, body = EXCLUDED.body, saved_at = EXCLUDED.saved_at";

    internal const string GetSnapshotSql = "SELECT revision, body, saved_at FROM kodo_snapshot WHERE id = 1";

    internal const string RecentStateChangesSql =
        "SELECT sequence, at_ticks, from_state, to_state FROM kodo_state_change ORDER BY sequence DESC LIMIT $1";

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
