using System.Text;
using System.Text.Json;
using Bazaar.Erasure.Abstractions;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Erasure;

/// <summary>
/// Appends to <c>legal_hold_event</c> (migration erasure/0001) with the audit row in the same transaction. A release locks the
/// hold id first, so two racing releases append one event.
/// </summary>
public sealed class PostgresLegalHoldStore(NpgsqlDataSource dataSource, ITransactionalAuditLog audit) : ILegalHoldStore
{
    private const string _insertSql =
        "INSERT INTO legal_hold_event (hold_id, action, scope_kind, scope_value, reason, actor, signature_key_id, occurred_at) " +
        "VALUES ($1, $2, $3, $4, $5, $6, $7, $8)";

    private const string _activeSql =
        "SELECT scope_kind, scope_value, reason, actor, created_at FROM active_legal_hold WHERE hold_id = $1";

    public async Task CreateAsync(
        Guid holdId, LegalHoldScopeKind kind, string scopeValue, string reason, string actor, string signatureKeyId, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await InsertAsync(conn, tx, holdId, "created", Kind(kind), scopeValue, reason, actor, signatureKeyId, at, ct).ConfigureAwait(false);
        await audit.AppendInTransactionAsync(conn, tx, Entry(at, actor, "legal_hold.created", holdId, Kind(kind), scopeValue, reason, signatureKeyId), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> ReleaseAsync(Guid holdId, string reason, string actor, string signatureKeyId, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = "legal_hold:" + holdId.ToString("D") });
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        string kind, scope;
        await using (var read = new NpgsqlCommand(_activeSql, conn, tx))
        {
            read.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = holdId });
            await using var r = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
                return false;
            kind = r.GetString(0);
            scope = r.GetString(1);
        }

        await InsertAsync(conn, tx, holdId, "released", kind, scope, reason, actor, signatureKeyId, at, ct).ConfigureAwait(false);
        await audit.AppendInTransactionAsync(conn, tx, Entry(at, actor, "legal_hold.released", holdId, kind, scope, reason, signatureKeyId), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<LegalHold>> ListActiveAsync(CancellationToken ct)
    {
        var list = new List<LegalHold>();
        await using var cmd = dataSource.CreateCommand(
            "SELECT hold_id, scope_kind, scope_value, reason, actor, created_at FROM active_legal_hold ORDER BY created_at, hold_id");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new LegalHold(
                r.GetGuid(0),
                Enum.Parse<LegalHoldScopeKind>(r.GetString(1), ignoreCase: true),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetFieldValue<DateTimeOffset>(5)));
        }

        return list;
    }

    private static string Kind(LegalHoldScopeKind kind) => kind.ToString().ToLowerInvariant();

    private static async Task InsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid holdId, string action, string kind, string scope, string reason, string actor, string keyId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_insertSql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = holdId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = action });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = reason });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = keyId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static AuditEntry Entry(DateTimeOffset at, string actor, string kind, Guid holdId, string scopeKind, string scopeValue, string reason, string keyId)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("hold", holdId.ToString("D"));
            w.WriteString("reason", reason);
            w.WriteString("scope_kind", scopeKind);
            w.WriteString("scope_value", scopeValue);
            w.WriteString("signer", keyId);
            w.WriteEndObject();
        }

        return new AuditEntry(at, actor, kind, Encoding.UTF8.GetString(ms.ToArray()));
    }
}
