namespace Bazaar.Kodo.Sentinel;

/// <summary>
/// The sentinel's own phases, written as strings so the status JSON keeps a stable shape (doc 46 7.8). These are not
/// Kodo's health states (section 8); a failing sentinel phase is the sentinel reporting that it could not verify a slot.
/// </summary>
public static class SentinelPhases
{
    /// <summary>Before the start check has run.</summary>
    public const string Starting = "starting";

    /// <summary>A verified slot is active and no probation is running.</summary>
    public const string Running = "running";

    /// <summary>A newly promoted slot is inside its probation window.</summary>
    public const string Probation = "probation";

    /// <summary>A verification or probation failure was reported. The active slot, if any, is verified.</summary>
    public const string Failing = "failing";

    /// <summary>Neither pointer verifies, so no slot runs. The journal and this status are all that is left.</summary>
    public const string Sealed = "sealed";
}
