using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Security.Taint;
using Npgsql;

namespace Bazaar.Server.Security.Approvals.Db;

/// <summary>
/// Reads and writes the <c>approval</c> table (migration 0011, doc 15). The service decides what changes; this class
/// stores rows and makes every status change a compare-and-set. Works under the runtime role (<c>bazaar_app</c>): it
/// needs INSERT, SELECT and UPDATE, never DELETE.
/// </summary>
public sealed class PostgresApprovalStore(NpgsqlDataSource dataSource) : IApprovalStore
{
    private const string _columns =
        "id, agent_id, session_id, tool, target, args_hash, payload::text, taint_sources::text, status, requested_at, " +
        "expires_at, decided_by, device_id, decided_at, signature, consumed_at";

    /// <inheritdoc />
    public async Task InsertAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO approval (id, agent_id, session_id, tool, target, args_hash, payload, taint_sources, status, " +
            "requested_at, expires_at, decided_by, device_id, decided_at, signature, consumed_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, CAST($7 AS jsonb), CAST($8 AS jsonb), $9, $10, $11, $12, $13, $14, $15, $16)");
        AddParameters(cmd, request, ToSql(request.Status));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ApprovalRequest?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM approval WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One UPDATE with the expected status in its WHERE clause. Postgres re-checks the WHERE after a concurrent update
    /// commits, so when several callers race from the same status, exactly one sees a changed row.
    /// </remarks>
    public async Task<bool> TryReplaceAsync(Guid id, ApprovalStatus expected, ApprovalRequest updated, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (updated.Id != id)
        {
            return false;
        }

        await using var cmd = dataSource.CreateCommand(
            "UPDATE approval SET agent_id = $2, session_id = $3, tool = $4, target = $5, args_hash = $6, " +
            "payload = CAST($7 AS jsonb), taint_sources = CAST($8 AS jsonb), status = $9, requested_at = $10, " +
            "expires_at = $11, decided_by = $12, device_id = $13, decided_at = $14, signature = $15, consumed_at = $16 " +
            "WHERE id = $1 AND status = $17");
        AddParameters(cmd, updated, ToSql(updated.Status));
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ToSql(expected) });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Binds one row to the INSERT/UPDATE parameter list ($1 id ... $16 consumed_at).</summary>
    private static void AddParameters(NpgsqlCommand cmd, ApprovalRequest r, string status)
    {
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = r.Id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = r.AgentId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = r.SessionId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = r.Tool });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = r.Target });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = r.ArgsHash });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = r.PayloadJson });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = SerializeTaint(r.TaintSources) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(r.RequestedAt) });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(r.ExpiresAt) });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = r.DecidedBy });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = r.DeviceId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = r.DecidedAt is { } d ? TruncateToMicroseconds(d) : null });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]?> { TypedValue = r.Signature });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = r.ConsumedAt is { } c ? TruncateToMicroseconds(c) : null });
    }

    private static ApprovalRequest Map(NpgsqlDataReader reader) => new(
        Id: reader.GetFieldValue<Guid>(0),
        AgentId: reader.GetString(1),
        SessionId: reader.GetFieldValue<Guid>(2),
        Tool: reader.GetString(3),
        Target: reader.IsDBNull(4) ? null : reader.GetString(4),
        ArgsHash: reader.GetFieldValue<byte[]>(5),
        PayloadJson: reader.GetString(6),
        TaintSources: DeserializeTaint(reader.GetString(7)),
        Status: FromSql(reader.GetString(8)),
        RequestedAt: reader.GetFieldValue<DateTimeOffset>(9),
        ExpiresAt: reader.GetFieldValue<DateTimeOffset>(10),
        DecidedBy: reader.IsDBNull(11) ? null : reader.GetString(11),
        DeviceId: reader.IsDBNull(12) ? null : reader.GetString(12),
        DecidedAt: reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        Signature: reader.IsDBNull(14) ? null : reader.GetFieldValue<byte[]>(14),
        ConsumedAt: reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15));

    /// <summary>Status to the text stored in the <c>status</c> column (CHECK list in migration 0011).</summary>
    private static string ToSql(ApprovalStatus status) => status switch
    {
        ApprovalStatus.Pending => "pending",
        ApprovalStatus.Approved => "approved",
        ApprovalStatus.Denied => "denied",
        ApprovalStatus.Expired => "expired",
        ApprovalStatus.Consumed => "consumed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown approval status."),
    };

    private static ApprovalStatus FromSql(string value) => value switch
    {
        "pending" => ApprovalStatus.Pending,
        "approved" => ApprovalStatus.Approved,
        "denied" => ApprovalStatus.Denied,
        "expired" => ApprovalStatus.Expired,
        "consumed" => ApprovalStatus.Consumed,
        _ => throw new InvalidOperationException($"Approval row has unknown status '{value}'."),
    };

    private static string SerializeTaint(ImmutableArray<TaintSource> sources)
    {
        var rows = new TaintSourceRow[sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            var s = sources[i];
            rows[i] = new TaintSourceRow(s.SourceId, s.Trust.ToString(), s.Raised.ToString());
        }

        return JsonSerializer.Serialize(rows, ApprovalJsonContext.Default.TaintSourceRowArray);
    }

    private static ImmutableArray<TaintSource> DeserializeTaint(string json)
    {
        var rows = JsonSerializer.Deserialize(json, ApprovalJsonContext.Default.TaintSourceRowArray)
            ?? throw new InvalidOperationException("Approval taint_sources is not a JSON array.");

        var builder = ImmutableArray.CreateBuilder<TaintSource>(rows.Length);
        foreach (var row in rows)
        {
            builder.Add(new TaintSource(
                row.SourceId,
                Enum.Parse<ContentTrust>(row.Trust, ignoreCase: false),
                Enum.Parse<TaintState>(row.Raised, ignoreCase: false)));
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Converts to UTC and drops sub-microsecond ticks, the precision Postgres stores.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset ts)
    {
        var utc = ts.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}

/// <summary>One element of the <c>taint_sources</c> JSON array (camelCase keys). Enums are stored by name.</summary>
internal sealed record TaintSourceRow(Guid SourceId, string Trust, string Raised);

/// <summary>Source-generated JSON for <see cref="TaintSourceRow"/>; no reflection on the read or write path.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(TaintSourceRow[]))]
internal sealed partial class ApprovalJsonContext : JsonSerializerContext
{
}
