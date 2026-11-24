namespace Bazaar.Server.Settings;

/// <summary>
/// One audit entry for a settings write attempt on a registered key (doc 03, doc 15 <c>audit_log</c>). Accepted changes
/// write one entry; held (PendingApproval) and rejected attempts write one too, with that outcome. Unknown keys write none,
/// because there is no key to audit.
/// </summary>
/// <param name="Key">Setting name.</param>
/// <param name="Scope">Scope the write targeted.</param>
/// <param name="ScopeId">Scope id; empty for install.</param>
/// <param name="ActorId">Who attempted the write.</param>
/// <param name="Outcome">Accepted, PendingApproval or Rejected.</param>
/// <param name="OldValueJson">Effective value before the write (the default when unset). Canonical JSON.</param>
/// <param name="NewValueJson">The proposed value: canonical JSON when it parsed, else the raw text.</param>
/// <param name="Reason">Why it was rejected or held; null when accepted.</param>
public sealed record SettingChangeAudit(
    string Key,
    SettingScope Scope,
    string ScopeId,
    string ActorId,
    SettingWriteOutcome Outcome,
    string? OldValueJson,
    string NewValueJson,
    string? Reason);

/// <summary>Receives audit entries. The Postgres writer (doc 15, A3) implements it; tests use <see cref="InMemorySettingAuditSink"/>.</summary>
public interface ISettingAuditSink
{
    Task AppendAsync(SettingChangeAudit change, CancellationToken cancellationToken = default);
}
