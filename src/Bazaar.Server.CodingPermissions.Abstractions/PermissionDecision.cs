namespace Bazaar.Server.CodingPermissions;

/// <summary>The three outcomes of a permission check.</summary>
public enum PermissionOutcome
{
    /// <summary>The call may run.</summary>
    Allow,

    /// <summary>The call needs an approval first.</summary>
    RequireApproval,

    /// <summary>The call is refused and no approval can change that.</summary>
    Deny,
}

/// <summary>What an approval would cover, shown to the approver.</summary>
/// <param name="Description">Visible scope text, such as "push agent/fix-x for this session".</param>
/// <param name="PerAction">True when the approval must bind to the call's args hash.</param>
public sealed record ApprovalScope(string Description, bool PerAction);

/// <summary>The result of <see cref="IToolPermissionGate.EvaluateAsync"/>.</summary>
/// <param name="Outcome">Allow, require approval, or deny.</param>
/// <param name="Reason">Why. Empty for a plain allow.</param>
/// <param name="Scope">Set only for <see cref="PermissionOutcome.RequireApproval"/>.</param>
public sealed record PermissionDecision(PermissionOutcome Outcome, string Reason, ApprovalScope? Scope = null)
{
    /// <summary>The call may run.</summary>
    public static PermissionDecision Allow(string reason = "") => new(PermissionOutcome.Allow, reason);

    /// <summary>The call needs an approval covering <paramref name="scope"/>.</summary>
    public static PermissionDecision RequireApproval(string reason, ApprovalScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(PermissionOutcome.RequireApproval, reason, scope);
    }

    /// <summary>The call is refused.</summary>
    public static PermissionDecision Deny(string reason) => new(PermissionOutcome.Deny, reason);
}
