namespace Bazaar.Roles.Abstractions;

/// <summary>Why a role change was refused. The first six are the floors of doc 48 section 4.1 and the rules around them.</summary>
public enum RoleRefusal
{
    /// <summary>The actor lacks <c>admin.roles</c>.</summary>
    NotAuthorized,

    /// <summary>At least one human owner must exist at all times.</summary>
    LastOwner,

    /// <summary>Agents never hold <c>admin.*</c> or the approval capability.</summary>
    AgentCapability,

    /// <summary>Only a human owner other than the target can grant the approval capability.</summary>
    ApprovalGrant,

    /// <summary>The owner role cannot be deleted, renamed, or lose its floor capabilities.</summary>
    ProtectedRole,

    /// <summary>A change that widens a human principal's grants was not approved by the gate.</summary>
    WideningNotApproved,

    /// <summary>The role, the principal or the owner is missing, or the input is invalid.</summary>
    Invalid,
}

/// <summary>A refused role change. Carries a stable <see cref="Refusal"/> for callers and tests.</summary>
public sealed class RoleFloorException(RoleRefusal refusal, string message) : InvalidOperationException(message)
{
    public RoleRefusal Refusal { get; } = refusal;
}
