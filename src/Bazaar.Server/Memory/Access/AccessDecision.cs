namespace Bazaar.Server.Memory.Access;

/// <summary>
/// The outcome of an access check, with the reason for audit and whether the read must be logged.
/// <see cref="Logged"/> is true only for owner reads of another agent's private area (docs/design/08-memory-system.md, section 8).
/// </summary>
/// <param name="Allowed">True when the read may proceed.</param>
/// <param name="Reason">Why the decision was made.</param>
/// <param name="Logged">True when the read must be written to the access log.</param>
public sealed record AccessDecision(bool Allowed, string Reason, bool Logged)
{
    /// <summary>An allowed read, logged only when the caller says so.</summary>
    public static AccessDecision Allow(string reason, bool logged = false) => new(true, reason, logged);

    /// <summary>A refused read. Never logged, so a refused probe leaves no trace of the path's content.</summary>
    public static AccessDecision Deny(string reason) => new(false, reason, false);
}
