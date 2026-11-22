using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Wire.Codec.MemoryPack;

/// <summary>Decode caps handed to the generated readers (doc 90 section 4.5).</summary>
internal sealed class MemoryPackLimits
{
    public MemoryPackLimits(WireDecodeLimits limits)
    {
        MaxDepth = limits.MaxDepth;
        MaxStringBytes = limits.MaxStringBytes;
        MaxItems = limits.MaxCollectionItems;
    }

    public int MaxDepth { get; }

    public int MaxStringBytes { get; }

    public int MaxItems { get; }
}
