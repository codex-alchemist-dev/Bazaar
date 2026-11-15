namespace Bazaar.Secrets;

public enum SecretAuditAction
{
    Put,
    Get,
    Delete,
    CryptoErase,
}

public enum SecretAuditOutcome
{
    Allowed,
    Denied,
}

/// <summary>
/// One audit entry. It names the caller, scope, secret name and purpose, and never carries a value.
/// The store writes it before the operation, so a failed write means the operation does not happen.
/// </summary>
public sealed record SecretAuditEvent(
    DateTimeOffset At,
    SecretAuditAction Action,
    SecretAuditOutcome Outcome,
    string Caller,
    string ScopeKey,
    string? Name,
    string? Purpose,
    string? Reason);

public interface ISecretAuditSink
{
    Task RecordAsync(SecretAuditEvent auditEvent, CancellationToken ct);
}
