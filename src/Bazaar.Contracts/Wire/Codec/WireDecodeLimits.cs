namespace Bazaar.Contracts.Wire.Codec;

/// <summary>
/// Decode caps every codec enforces (doc 90 section 4.5), so a hostile frame cannot cost more than a bounded amount of
/// work. They apply on top of the transport's <c>MaxFrameBytes</c> check, which runs before any decode.
/// </summary>
/// <param name="MaxDepth">Deepest nesting of arrays and objects.</param>
/// <param name="MaxStringBytes">Longest text or byte string, in encoded bytes.</param>
/// <param name="MaxCollectionItems">Most items in one array, or most entries in one object.</param>
public sealed record WireDecodeLimits(
    int MaxDepth = WireDecodeLimits.DefaultMaxDepth,
    int MaxStringBytes = WireDecodeLimits.DefaultMaxStringBytes,
    int MaxCollectionItems = WireDecodeLimits.DefaultMaxCollectionItems)
{
    /// <summary>Default nesting depth (setting <c>wire.decode.max_depth</c>).</summary>
    public const int DefaultMaxDepth = 32;

    /// <summary>Default string length in bytes: 1 MiB (setting <c>wire.decode.max_string_bytes</c>).</summary>
    public const int DefaultMaxStringBytes = 1_048_576;

    /// <summary>Default collection length (setting <c>wire.decode.max_collection_items</c>).</summary>
    public const int DefaultMaxCollectionItems = 100_000;

    /// <summary>The shipped defaults.</summary>
    public static WireDecodeLimits Default { get; } = new();
}
