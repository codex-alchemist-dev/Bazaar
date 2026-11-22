using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Wire.Codec.Protobuf;

/// <summary>Decode caps handed to the generated mappers (doc 90 section 4.5).</summary>
internal sealed class ProtobufLimits
{
    public ProtobufLimits(WireDecodeLimits limits)
    {
        MaxDepth = limits.MaxDepth;
        MaxStringBytes = limits.MaxStringBytes;
        MaxItems = limits.MaxCollectionItems;
    }

    public int MaxDepth { get; }

    public int MaxStringBytes { get; }

    public int MaxItems { get; }
}
