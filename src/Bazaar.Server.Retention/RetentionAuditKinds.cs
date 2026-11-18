namespace Bazaar.Server.Retention;

/// <summary>Audit entry kinds written by the sweeper. Payloads carry the category name, cutoff, counts and the hold decision only.</summary>
public static class RetentionAuditKinds
{
    public const string Swept = "retention.sweep";
    public const string Blocked = "retention.sweep_blocked";
    public const string FailedClosed = "retention.sweep_failed_closed";

    /// <summary>The actor recorded on every sweep entry.</summary>
    public const string Actor = "system:retention-sweeper";
}
