using Bazaar.Contracts.Gc;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Gc.Write;

/// <summary>
/// Appends to the GC log (docs/design/01, 04 "GC append"). One transaction per call:
/// <list type="number">
/// <item>Lock the <c>gc</c> row (<c>FOR UPDATE</c>) and look up the idempotency keys in the same round trip. The row lock is
/// what serialises writers per GC; because it is taken before anything is written, a repeated key is answered without
/// consuming a seq, and concurrent repeats see each other's committed row.</item>
/// <item>One batch: advance <c>head_seq</c> and insert the events, message rows and mentions.</item>
/// <item>Commit, then hand the new head to the notifier. NOTIFY is never sent inside the transaction (spike S3).</item>
/// </list>
/// A rollback undoes the counter with the rows, so a failed append leaves no gap. If the commit's outcome is unknown (the
/// connection dropped), the caller retries with the same key and gets the original event or a fresh append.
/// </summary>
public sealed class GcLogWriter(NpgsqlDataSource dataSource, IGcNotifier notifier, TimeProvider? timeProvider = null) : IGcLogWriter
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private const string _locksql = """
        SELECT head_seq, archived_at IS NOT NULL FROM gc WHERE gc_id = $1 FOR UPDATE
        """;

    private const string _keyssql = """
        SELECT k.ord, e.seq, e.event_id, e.created_at
        FROM unnest($2::uuid[], $3::text[]) WITH ORDINALITY AS k(author_id, client_msg_id, ord)
        JOIN gc_event e ON e.gc_id = $1 AND e.author_id = k.author_id AND e.client_msg_id = k.client_msg_id
        """;

    private const string _advancesql = "UPDATE gc SET head_seq = head_seq + $2 WHERE gc_id = $1";

    private const string _inserteventsql = """
        INSERT INTO gc_event (gc_id, seq, event_id, kind, author_kind, author_id, session_id, source_trust,
                              tainted_at_write, taint_sources, message_id, version, target_seq, reply_to_seq,
                              payload, client_msg_id, created_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, $11, $12, $13, $14, $15::jsonb, $16, $17)
        """;

    private const string _insertmessagesql = "INSERT INTO gc_message (gc_id, message_id, first_seq, author_id) VALUES ($1, $2, $3, $4)";

    private const string _insertversionsql = """
        INSERT INTO gc_message_version (gc_id, message_id, version, body_ct, key_id, editor_id, created_at)
        VALUES ($1, $2, 1, $3, $4, $5, $6)
        """;

    private const string _insertmentionssql = """
        INSERT INTO gc_mention (gc_id, seq, target_principal_id)
        SELECT $1, $2, t FROM unnest($3::uuid[]) AS t
        """;

    /// <inheritdoc />
    public async Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = await AppendManyAsync([request], ct).ConfigureAwait(false);
        return results[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return [];
        var gcId = Validate(requests);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var (head, existing) = await LockAndLookupAsync(conn, tx, gcId, requests, ct).ConfigureAwait(false);

        var results = new AppendResult[requests.Count];
        var fresh = new List<(int Index, long Seq)>();
        var next = head;
        for (var i = 0; i < requests.Count; i++)
        {
            if (existing.TryGetValue(i, out var found))
            {
                results[i] = new AppendResult(gcId, found.Seq, found.EventId, Duplicate: true, found.CreatedAt);
            }
            else
            {
                next++;
                fresh.Add((i, next));
            }
        }

        if (fresh.Count > 0)
        {
            var now = TruncateToMicroseconds(_time.GetUtcNow());
            await WriteAsync(conn, tx, gcId, requests, fresh, now, results, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        if (fresh.Count > 0)
            notifier.Notify(gcId, next);
        return results;
    }

    // timestamptz keeps microseconds; returning the same value a later read (or a duplicate answer) will see.
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset t) => new(t.UtcTicks - (t.UtcTicks % 10), TimeSpan.Zero);

    private static Guid Validate(IReadOnlyList<AppendRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests[0]);
        var gcId = requests[0].GcId;
        var keys = new HashSet<(Guid, string)>();
        foreach (var r in requests)
        {
            ArgumentNullException.ThrowIfNull(r);
            if (r.GcId != gcId)
                throw new ArgumentException("All events in one append must target the same GC.", nameof(requests));
            if (string.IsNullOrWhiteSpace(r.Kind))
                throw new ArgumentException("Kind is required.", nameof(requests));
            if (string.IsNullOrEmpty(r.ClientMsgId) || r.ClientMsgId.Length > AppendRequest.MaxClientMsgIdLength)
                throw new ArgumentException($"ClientMsgId must be 1 to {AppendRequest.MaxClientMsgIdLength} characters.", nameof(requests));
            if (!keys.Add((r.AuthorId, r.ClientMsgId)))
                throw new ArgumentException($"Duplicate idempotency key '{r.ClientMsgId}' within one append.", nameof(requests));
            var isMessage = r.Kind == GcEventKinds.Message;
            if (isMessage != (r.Message is not null))
                throw new ArgumentException(isMessage ? "A message event needs a Message body." : "Only a message event carries a Message body.", nameof(requests));
            if (r.Mentions.Count > 0 && !isMessage)
                throw new ArgumentException("Only a message event carries mentions.", nameof(requests));
        }

        return gcId;
    }

    private static async Task<(long Head, Dictionary<int, (long Seq, Guid EventId, DateTimeOffset CreatedAt)> Existing)> LockAndLookupAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid gcId, IReadOnlyList<AppendRequest> requests, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(conn, tx);
        var lockCmd = new NpgsqlBatchCommand(_locksql);
        lockCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        batch.BatchCommands.Add(lockCmd);

        var keysCmd = new NpgsqlBatchCommand(_keyssql);
        keysCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        keysCmd.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. requests.Select(r => r.AuthorId)] });
        keysCmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. requests.Select(r => r.ClientMsgId)] });
        batch.BatchCommands.Add(keysCmd);

        await using var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new GcNotFoundException(gcId);
        var head = reader.GetInt64(0);
        var archived = reader.GetBoolean(1);

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var existing = new Dictionary<int, (long, Guid, DateTimeOffset)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            existing[(int)reader.GetInt64(0) - 1] = (reader.GetInt64(1), reader.GetGuid(2), reader.GetFieldValue<DateTimeOffset>(3));

        // Archive blocks new events; answering a repeat of an event written before the archive stays allowed.
        if (archived && existing.Count < requests.Count)
            throw new GcArchivedException(gcId);
        return (head, existing);
    }

    private static async Task WriteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid gcId, IReadOnlyList<AppendRequest> requests,
        List<(int Index, long Seq)> fresh, DateTimeOffset now, AppendResult[] results, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(conn, tx);
        batch.BatchCommands.Add(Cmd(_advancesql, P(gcId), P((long)fresh.Count)));

        foreach (var (index, seq) in fresh)
        {
            var r = requests[index];
            var eventId = Guid.CreateVersion7();
            var messageId = r.Message is null ? (Guid?)null : Guid.CreateVersion7();
            batch.BatchCommands.Add(Cmd(
                _inserteventsql,
                P(gcId), P(seq), P(eventId), P(r.Kind), P(GcEnumText.ToText(r.AuthorKind)), P(r.AuthorId), N(r.SessionId),
                P(GcEnumText.ToText(r.SourceTrust)), P(r.TaintedAtWrite), N(r.TaintSourcesJson), N(messageId),
                N(r.Message is null ? (int?)null : 1), N(r.TargetSeq), N(r.ReplyToSeq), N(r.PayloadJson), P(r.ClientMsgId), P(now)));

            if (r.Message is { } body && messageId is { } mid)
            {
                batch.BatchCommands.Add(Cmd(_insertmessagesql, P(gcId), P(mid), P(seq), P(r.AuthorId)));
                batch.BatchCommands.Add(Cmd(_insertversionsql, P(gcId), P(mid), PBytes(body.BodyCiphertext), P(body.KeyId), P(r.AuthorId), P(now)));
                if (r.Mentions.Count > 0)
                    batch.BatchCommands.Add(Cmd(_insertmentionssql, P(gcId), P(seq), P<Guid[]>([.. r.Mentions.Distinct()])));
            }

            results[index] = new AppendResult(gcId, seq, eventId, Duplicate: false, now);
        }

        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static NpgsqlBatchCommand Cmd(string sql, params NpgsqlParameter[] parameters)
    {
        var cmd = new NpgsqlBatchCommand(sql);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        return cmd;
    }

    private static NpgsqlParameter<T> P<T>(T value) => new NpgsqlParameter<T> { TypedValue = value };

    private static NpgsqlParameter<byte[]> PBytes(ReadOnlyMemory<byte> value) =>
        new NpgsqlParameter<byte[]> { TypedValue = value.ToArray(), NpgsqlDbType = NpgsqlDbType.Bytea };

    private static NpgsqlParameter N<T>(T? value) where T : struct =>
        value is { } v ? new NpgsqlParameter<T> { TypedValue = v } : new NpgsqlParameter { Value = DBNull.Value };

    private static NpgsqlParameter N(string? value) =>
        value is null ? new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text } : new NpgsqlParameter<string> { TypedValue = value };
}
