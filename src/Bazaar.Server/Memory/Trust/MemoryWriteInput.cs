using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Trust;

/// <summary>What the server knows about a memory write when it picks the starting tier.</summary>
/// <param name="SourceTrust">Trust tag of the content or actor that produced the write.</param>
/// <param name="SessionState">Taint of the session that made the write. Soft taint does not change the tier.</param>
/// <param name="InheritsExternal">True when the lineage shows the write was built from external text.</param>
/// <param name="Actor">Who made the write. Only the owner may write directly at <see cref="MemoryTier.OwnerPromoted"/>.</param>
public sealed record MemoryWriteInput(
    ContentTrust SourceTrust,
    TaintState SessionState,
    bool InheritsExternal,
    MemoryActor Actor);
