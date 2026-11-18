namespace Bazaar.Server.Backup.Recovery;

/// <summary>
/// Verifies one FIDO2 hardware-key assertion from a quorum member (doc 48 section 7). Contract only: the WebAuthn
/// check is a separate work package. A passkey on a phone is not a quorum key and must be rejected by implementations.
/// </summary>
public interface IHardwareKeyAssertionVerifier
{
    /// <summary>True when <paramref name="assertion"/> is a valid user-verified assertion by <paramref name="memberId"/> for this case.</summary>
    bool Verify(string caseId, string memberId, ReadOnlyMemory<byte> assertion);
}

/// <summary>Delivers recovery alerts (doc 48 section 7). Contract only: sending is a separate work package.</summary>
public interface IRecoveryAlertSink
{
    /// <summary>Sends the alerts. Called by the host after the initiation event is audited, never by the state machine.</summary>
    Task SendAsync(IReadOnlyList<RecoveryAlert> alerts, CancellationToken ct = default);
}

/// <summary>Who an alert goes to.</summary>
public enum RecoveryAlertTarget
{
    /// <summary>An existing owner device, which can cancel the recovery.</summary>
    OwnerDevice,

    /// <summary>A backup owner.</summary>
    BackupOwner,
}

/// <summary>One alert to send for a recovery case. Repeats during the lock are scheduled by the host.</summary>
public sealed record RecoveryAlert(RecoveryAlertTarget Target, string RecipientId, string CaseId, DateTimeOffset EarliestCompletion);

/// <summary>An audit event to append for a transition (maps onto <c>AuditEntry</c>; the payload is a JSON document as text).</summary>
public sealed record RecoveryAuditEvent(string Kind, string Payload);
