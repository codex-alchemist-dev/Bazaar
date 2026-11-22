namespace Bazaar.Kodo.Runtime;

/// <summary>
/// The fixed names of the runtime layout (doc 46 section 6.2). The pointer files live in the runtime root on both
/// Windows and Linux, and the root is never a read-only slot mount, so the swap never needs a remount.
/// </summary>
public static class RuntimeLayout
{
    /// <summary>The first slot id.</summary>
    public const string SlotA = "slot-a";

    /// <summary>The second slot id.</summary>
    public const string SlotB = "slot-b";

    /// <summary>The active pointer file, in the runtime root.</summary>
    public const string PointerFileName = "kodo.pointer.json";

    /// <summary>The pointer that was active before the last swap, kept for rollback.</summary>
    public const string PreviousPointerFileName = "kodo.pointer.previous.json";

    /// <summary>The temp file written before a swap. A stray copy is removed at start.</summary>
    public const string PointerTempFileName = "kodo.pointer.json.tmp";

    /// <summary>True for the two slot ids this layout knows.</summary>
    public static bool IsSlotId(string? slotId) =>
        slotId is SlotA or SlotB;
}
