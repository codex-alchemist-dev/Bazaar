namespace Bazaar.Kodo.Sentinel;

/// <summary>
/// The cause codes the sentinel reports (doc 46 7.8). Each is a stable string, so the status JSON and the journal can be
/// read without this code. The detail field carries the finer status name from the verifier.
/// </summary>
public static class SentinelCauses
{
    /// <summary>The pointer file does not exist.</summary>
    public const string PointerMissing = "pointer_missing";

    /// <summary>The pointer file exists but could not be read.</summary>
    public const string PointerUnreadable = "pointer_unreadable";

    /// <summary>The pointer file is not a well-formed pointer, or names an unknown slot.</summary>
    public const string PointerMalformed = "pointer_malformed";

    /// <summary>The pointer does not verify: signature, key, or the manifest digest it names.</summary>
    public const string PointerNotVerified = "pointer_not_verified";

    /// <summary>The slot the pointer names has no files.</summary>
    public const string SlotMissing = "slot_missing";

    /// <summary>The slot's files could not be read.</summary>
    public const string SlotUnreadable = "slot_unreadable";

    /// <summary>The slot's manifest or files do not verify (signature, a changed file, or an unlisted file).</summary>
    public const string SlotNotVerified = "slot_not_verified";

    /// <summary>A promotion candidate failed verification, so the active pointer was not moved.</summary>
    public const string CandidateRejected = "candidate_rejected";

    /// <summary>A promotion candidate names the slot that is already active.</summary>
    public const string CandidateIsActive = "candidate_is_active";

    /// <summary>The promoted slot did not survive its probation window, or its files changed during it.</summary>
    public const string ProbationFailed = "probation_failed";

    /// <summary>A rollback was needed but the previous pointer names a slot that is gone.</summary>
    public const string PreviousSlotMissing = "previous_slot_missing";

    /// <summary>A rollback was needed but the previous pointer does not verify.</summary>
    public const string PreviousNotVerified = "previous_not_verified";

    /// <summary>A pointer file could not be replaced, so the swap did not complete.</summary>
    public const string PointerWriteFailed = "pointer_write_failed";
}
