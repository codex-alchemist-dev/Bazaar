namespace Bazaar.Server.Security.Quarantine;

/// <summary>The outcome of a release request.</summary>
/// <param name="Allowed">True when the record was released.</param>
/// <param name="Record">The record after the decision, or null when no record was found.</param>
/// <param name="Reason">Why the request was refused, or null when allowed.</param>
public sealed record QuarantineDecision(bool Allowed, QuarantineRecord? Record, string? Reason)
{
    /// <summary>A refused request.</summary>
    public static QuarantineDecision Refused(string reason, QuarantineRecord? record = null) => new(false, record, reason);

    /// <summary>An allowed request.</summary>
    public static QuarantineDecision Accepted(QuarantineRecord record) => new(true, record, null);
}
