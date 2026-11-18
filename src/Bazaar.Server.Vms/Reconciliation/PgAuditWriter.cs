using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Bazaar.Server.Vms.Reconciliation;

/// <summary>
/// Writes the <c>audit_log</c> table (migration 0004) for the VM component. The VM component must not reference Bazaar.Server,
/// so this writer repeats the hash chain of <c>AuditChain</c> and the append lock of <c>PostgresAuditLog</c>. Both writers use
/// the same advisory lock key, so their rows form one chain. The payload is <c>{"subject": ..., "detail": ...}</c>.
/// Works under the runtime role: it needs INSERT and SELECT, never UPDATE or DELETE.
/// </summary>
public sealed class PgAuditWriter(NpgsqlDataSource dataSource, string actor = "vm-component") : IAuditWriter
{
    /// <summary>Must equal <c>PostgresAuditLog</c>'s key, so the two writers serialise on one chain.</summary>
    private const long _appendLockKey = 0x4241554449544C47;

    private static readonly byte[] _genesisHash = new byte[SHA256.HashSizeInBytes];

    public async Task WriteAsync(AuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.Action);
        var payload = BuildPayload(record);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = _appendLockKey });
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var (headSeq, headHash) = await ReadHeadAsync(conn, tx, ct).ConfigureAwait(false);
        var canonical = await CanonicalPayloadAsync(conn, tx, payload, ct).ConfigureAwait(false);
        var ts = TruncateToMicroseconds(record.At);
        var seq = headSeq + 1;
        var hash = ComputeHash(seq, ts, actor, record.Action, canonical, headHash);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO audit_log (seq, ts, actor, kind, payload, prev_hash, hash) VALUES ($1, $2, $3, $4, CAST($5 AS jsonb), $6, $7)",
            conn, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = ts });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Action });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = canonical });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = headHash });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = hash });
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static string BuildPayload(AuditRecord record)
    {
        JsonNode? detail;
        try
        {
            detail = JsonNode.Parse(record.Detail);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Audit detail must be a JSON document.", nameof(record), ex);
        }
        if (detail is null)
            throw new ArgumentException("Audit detail must be a JSON document.", nameof(record));

        return new JsonObject
        {
            ["subject"] = record.Subject.ToString(),
            ["detail"] = detail,
        }.ToJsonString();
    }

    /// <summary>The newest seq and its hash. An empty log has seq 0 and the genesis hash, so the next row is seq 1.</summary>
    private static async Task<(long Seq, byte[] Hash)> ReadHeadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT seq, hash FROM audit_log ORDER BY seq DESC LIMIT 1", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (0, _genesisHash);
        return (reader.GetInt64(0), reader.GetFieldValue<byte[]>(1));
    }

    /// <summary>Lets Postgres normalise the JSON text, so a row read back hashes to the value it was written with.</summary>
    private static async Task<string> CanonicalPayloadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string payload, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT CAST($1 AS jsonb)::text", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = payload });
        return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Same algorithm as <c>AuditChain.ComputeHash</c>: SHA-256 over big-endian seq and UTC ticks, then length-prefixed
    /// actor, kind, payload and prev_hash. Keep the two in step; the audit verifier depends on it.
    /// </summary>
    private static byte[] ComputeHash(long seq, DateTimeOffset ts, string actor, string kind, string payload, byte[] prevHash)
    {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[8];

        BinaryPrimitives.WriteInt64BigEndian(number, seq);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, ts.UtcTicks);
        stream.Write(number);
        WriteField(stream, Encoding.UTF8.GetBytes(actor));
        WriteField(stream, Encoding.UTF8.GetBytes(kind));
        WriteField(stream, Encoding.UTF8.GetBytes(payload));
        WriteField(stream, prevHash);

        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteField(MemoryStream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    /// <summary>Cuts to microseconds, the precision Postgres stores, so the hashed timestamp is the one read back.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset ts)
    {
        var utc = ts.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
