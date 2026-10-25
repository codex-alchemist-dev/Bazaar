namespace Bazaar.Server.Security.Taint;

/// <summary>
/// How tainted a session is (docs/design/15-security-taint-and-approvals.md). Ordered: a session
/// only moves up, never back to <see cref="Clean"/>.
/// </summary>
public enum TaintState
{
    /// <summary>No untrusted input has reached the session.</summary>
    Clean = 0,

    /// <summary>Agent text from a tainted source. Changes no memory tier and does not block Tier 2.</summary>
    Soft = 1,

    /// <summary>External content has reached the session. Gates every Tier 2 action.</summary>
    Hard = 2,
}
