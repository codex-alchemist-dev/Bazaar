using Bazaar.ModulePermissions;

namespace Bazaar.Server.DevSandbox;

/// <summary>
/// The lexical target rules shared by the policy engine and every engine that pre-checks a call (doc 48 section 2.1 rule 1). They
/// are the module permission gate's rules (<see cref="ModuleTargetRules"/>, doc 48 section 1), so a sandbox run and the running
/// module can never read one target or one host two ways. The one sandbox-only form is the bare <c>*</c> grant, which a test
/// operator may write in a sandbox specification and which is not a module permission. No file system or network access: pure
/// string checks. A target that fails them is never allowed by any grant, including <c>*</c>.
/// </summary>
public static class SandboxTargetRules
{
    private const string _wildcard = "*";

    /// <summary>
    /// True when <paramref name="target"/> is a lexically clean path or opaque name, as <see cref="ModuleTargetRules.IsValidTarget"/>
    /// reads it: non-empty, within the length limit, no control, format, separator or unassigned characters, no backslash, no
    /// <c>*</c>, and every segment non-empty and not made only of dots and spaces.
    /// </summary>
    public static bool IsValidTarget(string? target) => ModuleTargetRules.IsValidTarget(target);

    /// <summary>
    /// Whether a grant target covers a requested target. A bare <c>*</c> covers every clean target; anything else is a module
    /// permission scope: exact, or a <c>/*</c> prefix matched by whole segments that does not cover the prefix itself. A grant that
    /// is not a valid scope (for example <c>/*</c> or <c>a/b*</c>) covers nothing.
    /// </summary>
    public static bool GrantTargetMatches(string grantTarget, string actionTarget)
    {
        if (grantTarget is null || !ModuleTargetRules.IsValidTarget(actionTarget))
        {
            return false;
        }

        return grantTarget == _wildcard || ModuleTargetRules.ScopeCovers(grantTarget, actionTarget);
    }

    /// <summary>
    /// Whether a network grant covers an already normalised host: a bare <c>*</c>, or the same host in its one spelling. There is
    /// no prefix or subdomain form.
    /// </summary>
    public static bool GrantHostMatches(string grantTarget, string normalizedHost)
    {
        if (grantTarget is null || normalizedHost is null)
        {
            return false;
        }

        if (!string.Equals(ModuleTargetRules.NormalizeHost(normalizedHost), normalizedHost, StringComparison.Ordinal))
        {
            return false;
        }

        if (grantTarget == _wildcard)
        {
            return true;
        }

        return ModuleTargetRules.NormalizeHost(grantTarget) is { } grantHost && string.Equals(grantHost, normalizedHost, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one spelling of a network host, as <see cref="ModuleTargetRules.NormalizeHost"/> gives it: a lower-case ASCII DNS name
    /// or a canonical IP literal. Null for anything else (trailing dot, port, userinfo, brackets, whitespace, non-ASCII, short or
    /// hexadecimal IPv4 forms, a scoped or IPv4-mapped IPv6 address).
    /// </summary>
    public static string? NormalizeHost(string? host) => ModuleTargetRules.NormalizeHost(host);
}
