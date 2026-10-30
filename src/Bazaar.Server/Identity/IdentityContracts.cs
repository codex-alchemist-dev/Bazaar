using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Identity;

/// <summary>Opaque lease id (doc 14, <c>credential_lease.lease_id</c>).</summary>
public readonly record struct LeaseId(Guid Value);

/// <summary>Opaque credential id (doc 14, <c>credential.credential_id</c>).</summary>
public readonly record struct CredentialId(Guid Value);

/// <summary>What a caller asks for: one agent, one service, one action, with a stated purpose (doc 14, "Access flow").</summary>
public sealed record AccessRequest(string AgentId, string Service, string Action, string Purpose);

/// <summary>The answer to <see cref="IIdentityService.RequestAccessAsync"/> (doc 14).</summary>
public abstract record AccessDecision;

/// <summary>Access granted; the lease is valid until <see cref="ExpiresAt"/>.</summary>
public sealed record Granted(LeaseId Lease, DateTimeOffset ExpiresAt) : AccessDecision;

/// <summary>Tier 2 action: a per-action owner approval is needed first.</summary>
public sealed record NeedsApproval(Guid ApprovalId, ToolTier Tier) : AccessDecision;

/// <summary>No account exists for this agent and service; a person must create it.</summary>
public sealed record HumanRequired(string Reason) : AccessDecision;

/// <summary>Refused by policy, taint, quarantine or a revoked kill switch.</summary>
public sealed record Denied(string Reason) : AccessDecision;

/// <summary>How the credential is released (doc 14, "Release modes").</summary>
public enum ReleaseMode
{
    /// <summary>The host makes the upstream call and returns only the result.</summary>
    BrokeredCall,

    /// <summary>A short-lived cookie or token is written into one browser session for one service.</summary>
    Injection,

    /// <summary>The owner-allowed session-cookie profile in the mounted folder.</summary>
    MountedProfile,
}

/// <summary>Kinds of credential stored in the Passport (doc 14, <c>credential.kind</c>).</summary>
public enum CredentialKind
{
    ProviderKey,
    AppKey,
    Owner,
    OAuth,
    Cookie,
    Ssh,
    Totp,
    Recovery,
}

/// <summary>
/// Scope for revocation (doc 14, <c>RevocationScope</c>). Global revokes everything; Agent revokes one agent's leases,
/// App tokens and VMs.
/// </summary>
public abstract record RevocationScope;

public sealed record AgentRevocation(string AgentId) : RevocationScope;

public sealed record GlobalRevocation : RevocationScope;

/// <summary>
/// The append-only audit sink (doc 15). Identity code calls <see cref="IAuditLog.AppendAsync"/> and must not issue
/// a lease when the append fails (doc 14, "Audit first").
/// </summary>
public interface IIdentityAudit
{
    Task AppendAsync(string kind, string payloadJson, CancellationToken ct);
}

/// <summary>Adapts <see cref="IAuditLog"/> to <see cref="IIdentityAudit"/>.</summary>
public sealed class AuditLogIdentityAudit(IAuditLog log, TimeProvider time) : IIdentityAudit
{
    public Task AppendAsync(string kind, string payloadJson, CancellationToken ct) =>
        log.AppendAsync(new AuditEntry(time.GetUtcNow(), "identity", kind, payloadJson), ct);
}

/// <summary>Read-only view of an agent's current taint and quarantine state, supplied by the taint and runtime layers.</summary>
public interface IAgentStateReader
{
    SessionTaint TaintFor(string agentId);

    bool IsQuarantined(string agentId);
}
