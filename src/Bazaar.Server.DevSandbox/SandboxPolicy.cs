namespace Bazaar.Server.DevSandbox;

/// <summary>
/// The deny-by-default policy engine for one run (doc 48 section 2.1 rule 1 and rule 4). Every action is decided against the run's
/// approved grants and, for network attempts, the egress allowlist. Nothing is allowed unless a rule below allows it.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>A syscall with an empty class needs no permission and is allowed (<c>no-permission-needed</c>).</item>
/// <item>A network attempt is allowed only when its host is in <see cref="SandboxRunSpec.EgressAllowlist"/> (case-insensitive) and a grant
/// matches its class and host (a grant target of <c>*</c> or the same host, case-insensitive). A host outside the allowlist is
/// denied as <c>egress-not-allowed</c>; a host inside it without a grant is denied as <c>no-grant</c>.</item>
/// <item>Every other action is allowed (<c>granted</c>) only when a grant matches its class and target. A grant target of <c>*</c>
/// matches every target. A grant target ending in <c>/*</c> matches any target that starts with the text before the <c>*</c>.
/// Any other grant target must equal the action target (ordinal). No match is denied as <c>no-grant</c>.</item>
/// <item>An action with an empty class that is not a syscall is denied as <c>no-grant</c>.</item>
/// </list>
/// </remarks>
public sealed class SandboxPolicy
{
    private const string _prefixSuffix = "/*";
    private const string _wildcard = "*";

    private readonly SandboxRunSpec _spec;

    /// <summary>Creates the policy for one run.</summary>
    /// <param name="spec">The run's spec, carrying the approved grants and egress allowlist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="spec"/> is null.</exception>
    public SandboxPolicy(SandboxRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _spec = spec;
    }

    /// <summary>Decides one action.</summary>
    /// <param name="action">The action the module under test attempted.</param>
    /// <param name="reason">A short machine-readable reason, recorded in the audit stream.</param>
    /// <returns><see cref="SandboxDecision.Allowed"/> or <see cref="SandboxDecision.Denied"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    public SandboxDecision Decide(SandboxAction action, out string reason)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.Kind == SandboxActionKind.Syscall && action.Class.Length == 0)
        {
            reason = "no-permission-needed";
            return SandboxDecision.Allowed;
        }

        if (action.Kind == SandboxActionKind.NetworkAttempt)
        {
            return DecideNetwork(action, out reason);
        }

        if (action.Class.Length == 0)
        {
            reason = "no-grant";
            return SandboxDecision.Denied;
        }

        if (HasGrant(action, prefixStyle: true))
        {
            reason = "granted";
            return SandboxDecision.Allowed;
        }

        reason = "no-grant";
        return SandboxDecision.Denied;
    }

    private SandboxDecision DecideNetwork(SandboxAction action, out string reason)
    {
        if (!IsOnAllowlist(action.Target))
        {
            reason = "egress-not-allowed";
            return SandboxDecision.Denied;
        }

        if (action.Class.Length == 0 || !HasGrant(action, prefixStyle: false))
        {
            reason = "no-grant";
            return SandboxDecision.Denied;
        }

        reason = "granted";
        return SandboxDecision.Allowed;
    }

    private bool IsOnAllowlist(string host)
    {
        foreach (var allowed in _spec.EgressAllowlist)
        {
            if (string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasGrant(SandboxAction action, bool prefixStyle)
    {
        foreach (var grant in _spec.Grants)
        {
            if (!string.Equals(grant.Class, action.Class, StringComparison.Ordinal))
            {
                continue;
            }

            if (prefixStyle ? TargetMatches(grant.Target, action.Target) : HostMatches(grant.Target, action.Target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TargetMatches(string grantTarget, string actionTarget)
    {
        if (grantTarget == _wildcard)
        {
            return true;
        }

        if (grantTarget.EndsWith(_prefixSuffix, StringComparison.Ordinal))
        {
            var prefix = grantTarget[..^1];
            return actionTarget.StartsWith(prefix, StringComparison.Ordinal);
        }

        return string.Equals(grantTarget, actionTarget, StringComparison.Ordinal);
    }

    private static bool HostMatches(string grantTarget, string actionTarget) =>
        grantTarget == _wildcard || string.Equals(grantTarget, actionTarget, StringComparison.OrdinalIgnoreCase);
}
