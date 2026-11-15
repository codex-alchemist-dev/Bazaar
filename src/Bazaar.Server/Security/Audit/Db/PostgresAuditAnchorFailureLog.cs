using System.Globalization;
using Bazaar.Server.Security.Audit.Anchors;
using Npgsql;

namespace Bazaar.Server.Security.Audit.Db;

/// <summary>
/// Finds <c>audit.anchor.failed</c> rows in <c>audit_log</c> (migration 0004) for one checkpoint and target. The failure row is
/// appended after the checkpoint's last row, so the query starts after <c>seq</c> = last sequence and reads only the newer part
/// of the log. Works under the runtime role (<c>bazaar_app</c>), which has SELECT on the log.
/// </summary>
public sealed class PostgresAuditAnchorFailureLog(NpgsqlDataSource dataSource) : IAuditAnchorFailureLog
{
    private const string _existsSql = """
        SELECT EXISTS (
            SELECT 1 FROM audit_log
            WHERE seq > $1 AND kind = $2 AND payload->>'last_seq' = $3 AND payload->>'target_id' = $4)
        """;

    /// <inheritdoc />
    public async Task<bool> HasRecordedFailureAsync(long lastSeq, string targetId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(_existsSql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = lastSeq });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = AuditAnchorService.FailedKind });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = lastSeq.ToString(CultureInfo.InvariantCulture) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = targetId });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }
}
