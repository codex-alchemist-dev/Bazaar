namespace Bazaar.Kodo.Sentinel;

/// <summary>The last slot verification the sentinel ran, for the status file.</summary>
/// <param name="Slot">The slot the check was about, or null when the pointer named no slot.</param>
/// <param name="Verified">True when the pointer and the slot verified.</param>
/// <param name="Cause">The cause code when it did not verify, otherwise null.</param>
/// <param name="CheckedAt">When the check ran, in UTC.</param>
public sealed record SentinelVerification(string? Slot, bool Verified, string? Cause, DateTimeOffset CheckedAt);

/// <summary>The last rollback, for the status file.</summary>
/// <param name="At">When the rollback happened, in UTC.</param>
/// <param name="FromSlot">The slot that was left, or null when unknown.</param>
/// <param name="ToSlot">The verified slot that is now active.</param>
/// <param name="Cause">The cause code that triggered the rollback.</param>
public sealed record SentinelRollback(DateTimeOffset At, string? FromSlot, string ToSlot, string Cause);

/// <summary>
/// What the sentinel reports on the loopback status file (doc 46 section 7.8): the running slot, the last verification, the
/// restart count and the last rollback. Every property is always written, even when null, so the JSON shape is stable.
/// Phase, cause and detail are strings from <see cref="SentinelPhases"/> and <see cref="SentinelCauses"/>.
/// </summary>
/// <param name="SchemaVersion">The shape version. <see cref="CurrentSchemaVersion"/> for this build.</param>
/// <param name="Phase">The sentinel phase.</param>
/// <param name="ActiveSlot">The verified slot that is active, or null when no slot runs.</param>
/// <param name="Cause">The cause code for a failing or sealed phase, otherwise null.</param>
/// <param name="Detail">A short detail for the cause, or null.</param>
/// <param name="LastVerification">The last verification, or null before the first check.</param>
/// <param name="RestartCount">Restarts decided since the sentinel started.</param>
/// <param name="LastRollback">The last rollback, or null when none has happened.</param>
public sealed record SentinelStatus(
    int SchemaVersion,
    string Phase,
    string? ActiveSlot,
    string? Cause,
    string? Detail,
    SentinelVerification? LastVerification,
    int RestartCount,
    SentinelRollback? LastRollback)
{
    /// <summary>The shape version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The status before the start check has run.</summary>
    public static SentinelStatus Starting { get; } = new(CurrentSchemaVersion, SentinelPhases.Starting, null, null, null, null, 0, null);
}
