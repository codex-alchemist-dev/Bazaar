namespace Bazaar.Server.Components.PrivacyAudit;

/// <summary>
/// Audit kinds for the privacy and remote-access events (doc 32 section 8 and the doc 15 approval entry). Dotted lowercase.
/// Each kind names the doc section that requires it. Only events a design doc names are listed; a kind is added here
/// when a doc asks for it.
/// </summary>
public static class PrivacyAuditKinds
{
    // Doc 32 section 8 "offers" and section 3 step 1.
    public const string PairingOfferCreated = "pairing.offer_created";

    // Doc 32 section 8 "pairing" and section 3 step 4 (approval mints the device record and credentials).
    public const string PairingIssued = "pairing.issued";

    // Doc 32 section 8 "revocation" and section 5 (revocation is immediate).
    public const string DeviceRevoked = "device.revoked";

    // Doc 32 section 8 "refresh reuse" and section 5 (reuse of a retired refresh credential revokes the device and audits it).
    public const string TokenReuseDetected = "token.reuse_detected";

    // Doc 32 section 8 "sign-in", section 4 (sign-in page). A failed attempt is recorded so breaches can be seen.
    public const string SignInSucceeded = "signin.succeeded";
    public const string SignInFailed = "signin.failed";

    // Doc 32 section 4 (`/signin/recover`, recovery-code sign-in) and section 8 (sign-in).
    public const string RecoveryCodeUsed = "recovery.code_used";

    // Doc 32 section 8 "DM access request/grant/deny" and section 6 (access request and grant or deny).
    public const string AccessRequestRequested = "access_request.requested";
    public const string AccessRequestDecided = "access_request.decided";

    // Doc 32 section 8 "role change".
    public const string RoleChanged = "role.changed";

    // Doc 32 section 8 "owner DM read" and section 6 (owner reads of DMs are audited).
    public const string OwnerDmRead = "dm.owner_read";

    // Doc 32 section 8 "export" and doc 15 (daily signed audit export).
    public const string ExportCompleted = "export.completed";

    // Doc 32 section 8 "erasure" and section 12 (erasure participants).
    public const string ErasureCompleted = "erasure.completed";

    // Doc 15 "Audit log and kill switch" (approvals are logged) and doc 32 section 6 (approvals come from a human).
    public const string ApprovalDecided = "approval.decided";

    // Doc 32 section 5a (owner approval key): enrolment, each use and revocation go to audit_log.
    public const string ApprovalKeyEnrolled = "approval_key.enrolled";
    public const string ApprovalKeyUsed = "approval_key.used";
    public const string ApprovalKeyRevoked = "approval_key.revoked";

    // Doc 32 section 7 (rate limiting: breaches are audited).
    public const string RateLimitDenied = "ratelimit.denied";

    /// <summary>Every kind in this class, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PairingOfferCreated,
        PairingIssued,
        DeviceRevoked,
        TokenReuseDetected,
        SignInSucceeded,
        SignInFailed,
        RecoveryCodeUsed,
        AccessRequestRequested,
        AccessRequestDecided,
        RoleChanged,
        OwnerDmRead,
        ExportCompleted,
        ErasureCompleted,
        ApprovalDecided,
        ApprovalKeyEnrolled,
        ApprovalKeyUsed,
        ApprovalKeyRevoked,
        RateLimitDenied,
    ];
}
