using System.Text.Json.Nodes;
using Bazaar.Secrets;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Secrets;

/// <summary>
/// Writes secret-store audit events into the shared hash-chained audit log (<see cref="IAuditLog"/>, migration common/0004),
/// so there is one log to verify and checkpoint. The payload carries names, scope keys and purposes, never values.
/// A failed append throws, which stops the secret operation, as the store requires.
/// </summary>
public sealed class AuditLogSecretSink(IAuditLog log) : ISecretAuditSink
{
    public Task RecordAsync(SecretAuditEvent auditEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var payload = new JsonObject
        {
            ["scope"] = auditEvent.ScopeKey,
            ["name"] = auditEvent.Name,
            ["purpose"] = auditEvent.Purpose,
            ["reason"] = auditEvent.Reason,
        };
        var kind = $"secrets.{auditEvent.Action.ToString().ToLowerInvariant()}.{auditEvent.Outcome.ToString().ToLowerInvariant()}";
        return log.AppendAsync(new AuditEntry(auditEvent.At, auditEvent.Caller, kind, payload.ToJsonString()), ct);
    }
}
