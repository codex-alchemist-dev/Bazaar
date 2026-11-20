namespace Bazaar.Kodo.Sentinel;

/// <summary>
/// The outcome of checking one pointer and the slot it names. A check passes only when <see cref="Cause"/> is null. The
/// <see cref="Detail"/> carries the verifier's status name, so the evidence keeps the finer reason.
/// </summary>
/// <param name="SlotId">The slot the pointer names, or null when the pointer named none.</param>
/// <param name="Cause">The cause code from <see cref="SentinelCauses"/>, or null when verified.</param>
/// <param name="Detail">The verifier's status name, or null.</param>
internal sealed record SlotCheck(string? SlotId, string? Cause, string? Detail)
{
    /// <summary>True when the pointer and the slot verified.</summary>
    public bool IsVerified => Cause is null;

    /// <summary>A passing check for <paramref name="slotId"/>.</summary>
    public static SlotCheck Passed(string slotId) => new(slotId, null, null);

    /// <summary>A failing check with its cause and detail.</summary>
    public static SlotCheck Failed(string? slotId, string cause, string? detail = null) => new(slotId, cause, detail);
}
