namespace Bazaar.Contracts.Gc;

/// <summary>
/// Event kinds seeded for v1. The kind is a string so other docs can register more rows in the
/// GC type registry without a code change; these constants name the ones the core writes.
/// </summary>
public static class GcEventKinds
{
    /// <summary>A new message (the first version).</summary>
    public const string Message = "message";

    /// <summary>A new version of an existing message.</summary>
    public const string MessageEdit = "message_edit";

    /// <summary>Membership, archive, access requests and decisions, tombstones, notes.</summary>
    public const string System = "system";

    /// <summary>An SDK-validated structured record: task update, PR event, decision, vote notice.</summary>
    public const string Structured = "structured";

    /// <summary>Targets an earlier sequence number and marks it quarantined.</summary>
    public const string Quarantine = "quarantine";

    /// <summary>An accepted reader-session hand-over.</summary>
    public const string Handover = "handover";

    /// <summary>Metadata for an external mail; the content stays with reader sessions.</summary>
    public const string ExternalMail = "external_mail";
}
