using System.Globalization;
using Bazaar.Install.Abstractions;
using Bazaar.PrivacyAudit;
using Bazaar.Server.Modules.LineMove;
using Bazaar.Server.Modules.Updates;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.Modules.Persistence;

/// <summary>
/// SQL-first access to the module manager's append-only tables (<c>modules/0001</c>): update decisions and line-move audit
/// events, and permission approvals (<c>modules/0002</c>). All tables refuse UPDATE, DELETE and TRUNCATE, so this repository only inserts and reads.
/// </summary>
public sealed class ModuleManagerRepository(NpgsqlDataSource dataSource, ITransactionalAuditLog audit) : IModuleInstallStore
{
    /// <summary>The largest page <see cref="ListDecisionsAsync"/> returns in one call.</summary>
    public const int MaxListLimit = 500;

    private const string _appendDecisionSql = """
        INSERT INTO module_update_decision (module_id, version, line, decision, reason, policy_id, decided_at, actor)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
        """;

    // Newest first, so a page shows what happened most recently. id breaks ties between decisions in the same instant.
    private const string _listDecisionsSql = """
        SELECT module_id, version, line, decision, reason, policy_id, decided_at, actor
        FROM module_update_decision
        WHERE module_id = $1
        ORDER BY decided_at DESC, id DESC
        LIMIT $2
        """;

    private const string _appendLineMoveEventSql = """
        INSERT INTO module_line_move_event
            (move_id, step, outcome, from_line, to_line, snapshot_id, approving_role, occurred_at, detail)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
        """;

    // Chronological, so the list reads as the move ran. id breaks ties between events in the same instant.
    private const string _listLineMoveEventsSql = """
        SELECT move_id, step, outcome, from_line, to_line, snapshot_id, approving_role, occurred_at, detail
        FROM module_line_move_event
        WHERE move_id = $1
        ORDER BY occurred_at, id
        """;

    private const string _appendApprovalSql = """
        INSERT INTO module_permission_approval
            (module_id, version, permissions, approval_digest_sha256, signer_key_id, signature, owner_actor, approved_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
        """;

    // Oldest first: the approvals of one version in the order the owner gave them.
    private const string _listApprovalsSql = """
        SELECT module_id, version, permissions, approval_digest_sha256, signer_key_id, signature, owner_actor, approved_at
        FROM module_permission_approval
        WHERE module_id = $1 AND version = $2
        ORDER BY approved_at, id
        """;

    /// <summary>Appends one update decision. Earlier decisions for the module are kept.</summary>
    public async Task AppendDecisionAsync(ModuleUpdateDecisionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var cmd = dataSource.CreateCommand(_appendDecisionSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.ModuleId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Version });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Line });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Decision.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Reason });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = record.PolicyId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = record.DecidedAt });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Actor });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The newest decisions for one module, newest first. <paramref name="limit"/> is 1 to <see cref="MaxListLimit"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is outside 1 to <see cref="MaxListLimit"/>.</exception>
    public async Task<IReadOnlyList<ModuleUpdateDecisionRecord>> ListDecisionsAsync(string moduleId, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxListLimit);

        await using var cmd = dataSource.CreateCommand(_listDecisionsSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = moduleId });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<ModuleUpdateDecisionRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new ModuleUpdateDecisionRecord(
                ModuleId: reader.GetString(0),
                Version: reader.GetString(1),
                Line: reader.GetString(2),
                Decision: Enum.Parse<UpdateDecision>(reader.GetString(3), ignoreCase: false),
                Reason: reader.GetString(4),
                PolicyId: reader.IsDBNull(5) ? null : reader.GetString(5),
                DecidedAt: reader.GetFieldValue<DateTimeOffset>(6),
                Actor: reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>
    /// Appends one permission approval and its audit entry in one transaction (doc 48 section 1). Both commit or neither does, so an
    /// approval is never recorded without its audit row. The caller has already checked the approval and signed it.
    /// </summary>
    public async Task RecordPermissionApprovalAsync(
        ModulePermissionApprovalRecord record,
        InstallAuditEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entry);
        AuditPayloadGuard.Ensure(entry.Payload);
        var auditEntry = new AuditEntry(entry.Ts, entry.Actor, entry.Kind, entry.Payload.ToJsonString());

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(_appendApprovalSql, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.ModuleId });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Version });
            cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. record.Permissions] });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.ApprovalDigestSha256Hex });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.SignerKeyId });
            cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = record.Signature });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.OwnerActor });
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = record.ApprovedAt });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await audit.AppendInTransactionAsync(conn, tx, auditEntry, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Appends one <c>module.installed</c> audit entry after the payload guard passes.</summary>
    public Task RecordInstalledAsync(InstallAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AuditPayloadGuard.Ensure(entry.Payload);
        return audit.AppendAsync(new AuditEntry(entry.Ts, entry.Actor, entry.Kind, entry.Payload.ToJsonString()), ct);
    }

    /// <summary>The approvals recorded for one module version, oldest first.</summary>
    public async Task<IReadOnlyList<ModulePermissionApprovalRecord>> ListPermissionApprovalsAsync(string moduleId, string version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        ArgumentNullException.ThrowIfNull(version);

        await using var cmd = dataSource.CreateCommand(_listApprovalsSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = moduleId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = version });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<ModulePermissionApprovalRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new ModulePermissionApprovalRecord(
                ModuleId: reader.GetString(0),
                Version: reader.GetString(1),
                Permissions: reader.GetFieldValue<string[]>(2),
                ApprovalDigestSha256Hex: reader.GetString(3),
                SignerKeyId: reader.GetString(4),
                Signature: reader.GetFieldValue<byte[]>(5),
                OwnerActor: reader.GetString(6),
                ApprovedAt: reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return rows;
    }

    /// <summary>
    /// Appends one line-move audit event. The step and the outcome are stored under their enum names; the lines are stored as
    /// text (the line numbers, invariant culture).
    /// </summary>
    public async Task AppendLineMoveEventAsync(LineMoveAuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        await using var cmd = dataSource.CreateCommand(_appendLineMoveEventSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = auditEvent.MoveId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = auditEvent.Step.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = auditEvent.Kind.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = auditEvent.FromLine.ToString(CultureInfo.InvariantCulture) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = auditEvent.ToLine.ToString(CultureInfo.InvariantCulture) });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = auditEvent.SnapshotId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = auditEvent.ApprovingRole });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = auditEvent.At });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = auditEvent.Detail });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The audit events of one line move, in the order they were written.</summary>
    public async Task<IReadOnlyList<LineMoveAuditEvent>> ListLineMoveEventsAsync(string moveId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(moveId);

        await using var cmd = dataSource.CreateCommand(_listLineMoveEventsSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = moveId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<LineMoveAuditEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new LineMoveAuditEvent(
                MoveId: reader.GetString(0),
                Step: Enum.Parse<LineMoveStep>(reader.GetString(1), ignoreCase: false),
                Kind: Enum.Parse<LineMoveAuditKind>(reader.GetString(2), ignoreCase: false),
                FromLine: int.Parse(reader.GetString(3), NumberStyles.Integer, CultureInfo.InvariantCulture),
                ToLine: int.Parse(reader.GetString(4), NumberStyles.Integer, CultureInfo.InvariantCulture),
                ApprovingRole: reader.GetString(6),
                SnapshotId: reader.IsDBNull(5) ? null : reader.GetString(5),
                Detail: reader.GetString(8),
                At: reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return rows;
    }
}
