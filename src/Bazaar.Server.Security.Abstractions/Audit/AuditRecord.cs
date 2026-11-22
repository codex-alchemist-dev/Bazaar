namespace Bazaar.Server.Security.Audit;

/// <summary>One row of the audit log (doc 15 <c>audit_log</c>). Hashes are SHA-256 over the canonical encoding in <see cref="AuditChain"/>.</summary>
public sealed record AuditRecord(
    long Seq,
    DateTimeOffset Ts,
    string Actor,
    string Kind,
    string Payload,
    byte[] PrevHash,
    byte[] Hash);
