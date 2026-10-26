using System.Runtime.CompilerServices;
using Npgsql;

namespace Bazaar.Server.Security.Audit.Db;

/// <summary>
/// Writes and reads the <c>audit_log</c> table (migration 0004, doc 15). Hashing is <see cref="AuditChain"/>; this class
/// only persists the records and reads them back. Works under the runtime role (<c>bazaar_app</c>): it needs INSERT and
/// SELECT, never UPDATE or DELETE.
/// </summary>
public sealed class PostgresAuditLog(NpgsqlDataSource dataSource) : IAuditLog
{
    /// <summary>Advisory-lock key that serialises appends. Distinct from the migration lock and the GC locks ("BAUDITLG").</summary>
    private const long _appendLockKey = 0x4241554449544C47;

    private const string _columns = "seq, ts, actor, kind, payload::text, prev_hash, hash";

    /// <summary>
    /// Appends one entry. The transaction takes a transaction-scoped advisory lock first, so concurrent appenders queue;
    /// the head read then sees every committed row. The payload is canonicalised by Postgres (jsonb text), so a row read
    /// back hashes to the same value it was written with. Timestamps are cut to microseconds, the precision Postgres stores.
    /// </summary>
    public async Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(e.Actor);
        ArgumentNullException.ThrowIfNull(e.Kind);
        ArgumentNullException.ThrowIfNull(e.Payload);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = _appendLockKey });
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var head = await ReadHeadAsync(conn, tx, ct).ConfigureAwait(false);
        var canonical = await CanonicalPayloadAsync(conn, tx, e.Payload, ct).ConfigureAwait(false);
        var record = AuditChain.Append(head, TruncateToMicroseconds(e.Ts), e.Actor, e.Kind, canonical);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO audit_log (seq, ts, actor, kind, payload, prev_hash, hash) VALUES ($1, $2, $3, $4, CAST($5 AS jsonb), $6, $7)", conn, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = record.Seq });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = record.Ts });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Actor });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Kind });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Payload });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = record.PrevHash });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = record.Hash });
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The newest record, or null for an empty log.</summary>
    public async Task<AuditRecord?> ReadHeadAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadHeadAsync(conn, null, ct).ConfigureAwait(false);
    }

    /// <summary>Every record in seq order. Each read is one statement, so the result is a consistent snapshot.</summary>
    public async IAsyncEnumerable<AuditRecord> StreamAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT {_columns} FROM audit_log ORDER BY seq", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            yield return Map(reader);
    }

    /// <summary>Reads the whole log and runs <see cref="AuditChain.Verify"/> over it.</summary>
    public async Task<AuditVerification> VerifyAsync(byte[]? expectedHead = null, CancellationToken ct = default) =>
        AuditChain.Verify(await ReadAllAsync(ct).ConfigureAwait(false), expectedHead);

    /// <summary>The whole log as JSON lines (<see cref="AuditExport"/>), in seq order.</summary>
    public async Task<string> ExportAsync(CancellationToken ct = default) =>
        AuditExport.ToJsonLines(await ReadAllAsync(ct).ConfigureAwait(false));

    private async Task<IReadOnlyList<AuditRecord>> ReadAllAsync(CancellationToken ct)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in StreamAsync(ct).ConfigureAwait(false))
            records.Add(record);
        return records;
    }

    private static async Task<AuditRecord?> ReadHeadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {_columns} FROM audit_log ORDER BY seq DESC LIMIT 1", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Lets Postgres normalise the JSON text; rejects a payload that is not JSON.</summary>
    private static async Task<string> CanonicalPayloadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string payload, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT CAST($1 AS jsonb)::text", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = payload });
        try
        {
            return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidTextRepresentation)
        {
            throw new ArgumentException("Audit payload must be a JSON document.", nameof(payload), ex);
        }
    }

    private static AuditRecord Map(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetFieldValue<DateTimeOffset>(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetFieldValue<byte[]>(5),
        reader.GetFieldValue<byte[]>(6));

    /// <summary>
    /// Converts to UTC (Npgsql writes only UTC offsets to timestamptz) and drops sub-microsecond ticks, so the hashed
    /// timestamp equals the one Postgres stores and returns. The hash covers UTC ticks, so the offset does not change it.
    /// </summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset ts)
    {
        var utc = ts.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
