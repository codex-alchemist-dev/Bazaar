using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bazaar.PrivacyAudit;
using Bazaar.Server.Security.Audit;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Constellation.Memory.ReadLog;

/// <summary>
/// The Postgres owner-read log (docs/design/constellation-memory-graph.md, "Owner-read access log"): the append-only table
/// <c>constellation_memory_read_log</c> (migration constellation/0003) plus one <c>memory.owner_read</c> entry per row in the audit
/// hash chain, all in one transaction. Any failure surfaces as <see cref="MemoryReadLogException"/> so the read is refused. The runtime
/// role needs INSERT and SELECT only.
/// </summary>
public sealed class PgMemoryReadLog : IMemoryReadLog, IMemoryReadLogReader
{
    /// <summary>The audit kind appended for every row.</summary>
    public const string AuditKind = "memory.owner_read";

    private const string _columns =
        "read_id, at, reader_principal, reader_session, route, subject_agent_id, operation, note_ids, doc_ids, ids_truncated, result_count, taint_tags, purpose, audit_seq";

    private readonly NpgsqlDataSource _db;
    private readonly ITransactionalAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task<int>> _maxIds;

    /// <param name="db">The data source (runtime role).</param>
    /// <param name="audit">The hash-chained audit log; rows are appended inside the same transaction.</param>
    /// <param name="time">Clock for the row time; <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="maxIds">
    /// Ids stored per row (a setting, <see cref="MemoryReadLogSettings.MaxIdsPerRowKey"/>); null means the shipped default. Clamped to
    /// the hard cap. A read of more ids stores the first ids, the full <c>result_count</c> and <c>ids_truncated</c>.
    /// </param>
    public PgMemoryReadLog(
        NpgsqlDataSource db,
        ITransactionalAuditLog audit,
        TimeProvider? time = null,
        Func<CancellationToken, Task<int>>? maxIds = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _audit = audit;
        _time = time ?? TimeProvider.System;
        _maxIds = maxIds ?? (_ => Task.FromResult(MemoryReadLogSettings.MaxIdsPerRowDefault));
    }

    /// <inheritdoc />
    public async Task RecordAsync(IReadOnlyList<MemoryReadEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        foreach (MemoryReadEntry entry in entries)
        {
            Validate(entry);
        }

        try
        {
            int cap = Math.Clamp(await _maxIds(ct).ConfigureAwait(false), 1, MemoryReadLogSettings.MaxIdsPerRowHardCap);
            await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            DateTimeOffset at = _time.GetUtcNow();
            foreach (MemoryReadEntry entry in entries)
            {
                await InsertAsync(conn, tx, entry, at, cap, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MemoryReadLogException)
        {
            // Never echo the cause to the caller: the entry holds ids only, but the refusal must not depend on what failed.
            throw new MemoryReadLogException("The memory read could not be logged.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<MemoryReadLogPage> ListAsync(MemoryReadLogQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        int limit = Math.Clamp(query.Limit, 1, MemoryReadLogQuery.MaxLimit);

        await using NpgsqlCommand cmd = _db.CreateCommand($"""
            SELECT {_columns} FROM constellation_memory_read_log
            WHERE ($1::text IS NULL OR subject_agent_id = $1)
              AND ($2::timestamptz IS NULL OR (at, read_id) < ($2, $3))
            ORDER BY at DESC, read_id DESC
            LIMIT $4
            """);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)query.SubjectAgentId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)query.After?.At ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)query.After?.ReadId ?? "" });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit + 1 });

        var records = new List<MemoryReadLogRecord>();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                records.Add(Map(reader));
            }
        }

        if (records.Count <= limit)
        {
            return new MemoryReadLogPage(records, null);
        }

        records.RemoveAt(records.Count - 1);
        MemoryReadLogRecord last = records[^1];
        return new MemoryReadLogPage(records, new MemoryReadLogCursor(last.At, last.ReadId));
    }

    private async Task InsertAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        MemoryReadEntry entry,
        DateTimeOffset at,
        int cap,
        CancellationToken ct)
    {
        string readId = ReadIds.New(at);
        string[] noteIds = [.. entry.NoteIds.Distinct(StringComparer.Ordinal).Take(cap)];
        string[] docIds = [.. entry.DocIds.Distinct(StringComparer.Ordinal).Take(cap)];
        int resultCount = Math.Max(entry.DocIds.Distinct(StringComparer.Ordinal).Count(), entry.NoteIds.Distinct(StringComparer.Ordinal).Count());
        bool truncated = resultCount > Math.Max(noteIds.Length, docIds.Length);
        string[] tags = [.. entry.TaintTags.Distinct(StringComparer.Ordinal)];

        var payload = new JsonObject
        {
            ["read_id"] = readId,
            ["route"] = entry.Route,
            ["session"] = entry.ReaderSession,
            ["subject_agent_id"] = entry.SubjectAgentId,
            ["operation"] = entry.Operation,
            ["result_count"] = resultCount,
            ["ids_truncated"] = truncated,
            ["taint_tags"] = new JsonArray([.. tags.Select(t => (JsonNode?)JsonValue.Create(t))]),
            ["note_ids"] = new JsonArray([.. noteIds.Select(t => (JsonNode?)JsonValue.Create(t))]),
            ["doc_ids"] = new JsonArray([.. docIds.Select(t => (JsonNode?)JsonValue.Create(t))]),
        };
        AuditPayloadGuard.Ensure(payload);

        await _audit.AppendInTransactionAsync(
            conn, tx, new AuditEntry(at, entry.ReaderPrincipal, AuditKind, payload.ToJsonString()), ct).ConfigureAwait(false);

        // The audit append holds the chain's advisory lock until this transaction ends, so the head is the row just appended.
        long auditSeq;
        await using (var head = new NpgsqlCommand("SELECT max(seq) FROM audit_log", conn, tx))
        {
            auditSeq = (long)(await head.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? throw new MemoryReadLogException("The audit entry was not appended."));
        }

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO constellation_memory_read_log
                ({_columns})
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            """, conn, tx);
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = readId });
        insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at.ToUniversalTime() });
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entry.ReaderPrincipal });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)entry.ReaderSession ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entry.Route });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)entry.SubjectAgentId ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entry.Operation });
        insert.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = noteIds });
        insert.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = docIds });
        insert.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = truncated });
        insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = resultCount });
        insert.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = tags });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)entry.Purpose ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = auditSeq });
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Validate(MemoryReadEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ReaderPrincipal);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Route);
        ArgumentNullException.ThrowIfNull(entry.NoteIds);
        ArgumentNullException.ThrowIfNull(entry.DocIds);
        ArgumentNullException.ThrowIfNull(entry.TaintTags);
        if (!MemoryReadOperations.IsKnown(entry.Operation))
        {
            throw new ArgumentException($"Unknown read operation '{entry.Operation}'.", nameof(entry));
        }
    }

    private static MemoryReadLogRecord Map(NpgsqlDataReader r) => new(
        r.GetString(0),
        r.GetFieldValue<DateTimeOffset>(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.GetString(6),
        r.GetFieldValue<string[]>(7),
        r.GetFieldValue<string[]>(8),
        r.GetBoolean(9),
        r.GetInt32(10),
        r.GetFieldValue<string[]>(11),
        r.IsDBNull(12) ? null : r.GetString(12),
        r.GetInt64(13));
}

/// <summary>ULIDs for read ids: 48-bit millisecond time plus 80 random bits, Crockford base32, 26 characters, sortable by time.</summary>
internal static class ReadIds
{
    private const string _alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(DateTimeOffset at)
    {
        ulong ms = (ulong)at.ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        Span<char> chars = stackalloc char[26];
        for (int i = 9; i >= 0; i--)
        {
            chars[i] = _alphabet[(int)(ms & 31)];
            ms >>= 5;
        }

        System.Numerics.BigInteger value = new(random, isUnsigned: true, isBigEndian: true);
        for (int i = 25; i >= 10; i--)
        {
            chars[i] = _alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }
}
