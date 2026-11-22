namespace Bazaar.Client.Tui.Dashboard;

// The write ports of the resident TUI (doc 58 sections 3 and 5). Starting a rebuild and committing a module selection use
// IAdminModuleSelection from doc 51. Doc 51 has no port for revoking a person or a device or for deciding an approval (the web panel
// reads those sections only), so the three below are added here, small, for the owning modules (identity, device tokens, approvals) to
// implement, and move to the web admin port set if the panel ever adopts them. The caller has already checked the capability, taken
// the confirmation and written the audit entry; a port still refuses what its own rules refuse (the last owner cannot be revoked).
// None of these types carries a secret except the step-up proof, which only travels to the approval module and is never logged.

/// <summary>Outcome of a write port. <see cref="Reason"/> is a short plain-text reason for a refusal and never a secret or a stack trace.</summary>
public sealed record OpsWriteResult(bool Done, string? Reason = null);

/// <summary>Suspends a person (reversible, PROPOSED): their devices and approval keys are revoked at once (doc 32, revocation is immediate) and they cannot sign in or refresh. Implemented by the identity module.</summary>
public interface IOpsMemberRevoker
{
    Task<OpsWriteResult> RevokeMemberAsync(Guid actorPrincipalId, Guid principalId, CancellationToken ct = default);
}

/// <summary>Sets a suspended person active again (the reverse of <see cref="IOpsMemberRevoker"/>). Their revoked devices stay revoked, so they pair again.</summary>
public interface IOpsMemberRestorer
{
    Task<OpsWriteResult> RestoreMemberAsync(Guid actorPrincipalId, Guid principalId, CancellationToken ct = default);
}

/// <summary>Revokes one device through the same path as an owner revoking it from a client (<c>ITokenService.RevokeDeviceAsync</c>, doc 32). Implemented by the device token module.</summary>
public interface IOpsDeviceRevoker
{
    Task<OpsWriteResult> RevokeDeviceAsync(Guid actorPrincipalId, Guid deviceId, CancellationToken ct = default);
}

/// <summary>
/// Gives or refuses a pending approval (doc 15, doc 32 section 5a). An approval needs the step-up proof (a signed owner approval from a
/// device key) that the approval module verifies; a refusal needs none, since refusing only ever lowers risk (a TUI rule, PROPOSED).
/// </summary>
public interface IOpsApprovalDecider
{
    Task<OpsWriteResult> DecideAsync(Guid actorPrincipalId, string approvalId, bool approve, string? stepUpProof, CancellationToken ct = default);
}

/// <summary>What the step-up is asked to cover. <see cref="Subject"/> is the id of the thing acted on, so the proof binds to it.</summary>
public sealed record OpsStepUpRequest(Guid ActorPrincipalId, string Action, string Subject);

/// <summary>The step-up answer. <see cref="Proof"/> is opaque, goes only to the approval module and is never drawn, logged or audited.</summary>
public sealed record OpsStepUpResult(bool Granted, string? Proof = null, string? Reason = null);

/// <summary>
/// The approval step-up (doc 51 "normal approval flow where doc 15 requires one", doc 32 section 5a). A terminal holds no hardware-backed
/// approval key, so the implementation hands the request to a device that does (the owner's phone or Windows client) and waits for the
/// signed answer. When nothing is wired, approving is refused and the operator is told to use a client.
/// </summary>
public interface IOpsStepUp
{
    Task<OpsStepUpResult> RequestAsync(OpsStepUpRequest request, CancellationToken ct = default);
}

/// <summary>The audit kinds the TUI adds to the doc 51 ones (<c>AdminAuditKinds</c>). Fields carry ids and names, never a secret.</summary>
public static class OpsAuditKinds
{
    public const string MemberRevoke = "admin.tui.member.revoke";
    public const string MemberRestore = "admin.tui.member.restore";
    public const string DeviceRevoke = "admin.tui.device.revoke";
    public const string ApprovalDecide = "admin.tui.approval.decide";
}
