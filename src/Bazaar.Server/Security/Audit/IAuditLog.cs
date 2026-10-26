namespace Bazaar.Server.Security.Audit;

/// <summary>Sink for audit entries (doc 15 <c>IAuditLog</c>). The Postgres implementation is <c>Db.PostgresAuditLog</c>.</summary>
public interface IAuditLog
{
    /// <summary>Appends one entry at the head of the chain. Completes when the row is committed.</summary>
    Task AppendAsync(AuditEntry e, CancellationToken ct = default);
}

/// <summary>Input to <see cref="IAuditLog.AppendAsync"/>. <see cref="Payload"/> is a JSON document as text; the log stores its canonical form.</summary>
public sealed record AuditEntry(DateTimeOffset Ts, string Actor, string Kind, string Payload);
