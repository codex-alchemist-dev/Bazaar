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
/// <item>Targets are canonicalised lexically before any match (no file-system access). A target that is empty, holds a control,
/// format or separator character (NUL, newline, bidi override), a backslash, an empty segment (<c>//</c>), or a segment made only of
/// dots and spaces (<c>.</c>, <c>..</c>, <c>...</c>) is denied as <c>invalid-target</c>. One leading and one trailing <c>/</c> are
/// allowed. A prefix grant <c>a/b/*</c> matches by whole <c>/</c>-separated segments, so <c>a/bc</c> does not match it. Matching stays
/// ordinal and case-sensitive. Class comparison is exact and ordinal.</item>
/// <item>A network target must be a plain DNS name (ASCII letters, digits and hyphens in labels, lower-case after folding ASCII case,
/// no trailing dot, port, userinfo, brackets, whitespace or non-ASCII; punycode labels are accepted, Unicode labels are not) or a
/// canonical IP literal (the text equals the address's own canonical form). Anything else is denied as <c>invalid-target</c>.</item>
/// <item>A grant target of <c>*</c> is honoured for every class (doc 48 section 1: <c>*</c> means every target of the class), but never
/// for a target that fails the checks above.</item>
/// </list>
/// </remarks>
public sealed class SandboxPolicy
{
    private const string _invalidTarget = "invalid-target";

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

        var actionClass = action.Class ?? string.Empty;
        if (action.Kind == SandboxActionKind.Syscall && actionClass.Length == 0)
        {
            reason = "no-permission-needed";
            return SandboxDecision.Allowed;
        }

        if (action.Kind == SandboxActionKind.NetworkAttempt)
        {
            return DecideNetwork(action, out reason);
        }

        if (!SandboxTargetRules.IsValidTarget(action.Target))
        {
            reason = _invalidTarget;
            return SandboxDecision.Denied;
        }

        if (actionClass.Length == 0)
        {
            reason = "no-grant";
            return SandboxDecision.Denied;
        }

        foreach (var grant in _spec.Grants)
        {
            if (string.Equals(grant.Class, actionClass, StringComparison.Ordinal) && SandboxTargetRules.GrantTargetMatches(grant.Target, action.Target))
            {
                reason = "granted";
                return SandboxDecision.Allowed;
            }
        }

        reason = "no-grant";
        return SandboxDecision.Denied;
    }

    private SandboxDecision DecideNetwork(SandboxAction action, out string reason)
    {
        var host = SandboxTargetRules.NormalizeHost(action.Target);
        if (host is null)
        {
            reason = _invalidTarget;
            return SandboxDecision.Denied;
        }

        if (!IsOnAllowlist(host))
        {
            reason = "egress-not-allowed";
            return SandboxDecision.Denied;
        }

        var actionClass = action.Class ?? string.Empty;
        if (actionClass.Length == 0)
        {
            reason = "no-grant";
            return SandboxDecision.Denied;
        }

        foreach (var grant in _spec.Grants)
        {
            if (string.Equals(grant.Class, actionClass, StringComparison.Ordinal) && SandboxTargetRules.GrantHostMatches(grant.Target, host))
            {
                reason = "granted";
                return SandboxDecision.Allowed;
            }
        }

        reason = "no-grant";
        return SandboxDecision.Denied;
    }

    private bool IsOnAllowlist(string host)
    {
        foreach (var allowed in _spec.EgressAllowlist)
        {
            if (SandboxTargetRules.NormalizeHost(allowed) is { } normalized && string.Equals(normalized, host, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
