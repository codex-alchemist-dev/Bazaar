namespace Bazaar.Server.Terminal.Stream;

/// <summary>
/// Per-connection settings for <see cref="TerminalConnection"/>. <see cref="Normalize"/> clamps every value: a setting may tighten
/// a limit but never loosen it past its ceiling, and a nonsense value falls back to the safe bound.
/// </summary>
public sealed record TerminalConnectionOptions
{
    /// <summary>Hard ceiling of the malformed-frame budget (doc 55, section 6).</summary>
    public const int MaxMalformedCeiling = 32;

    /// <summary>Outbound frames queued per connection before the oldest output frame is dropped. Default 256, range 8 to 4096.</summary>
    public int OutboundQueueFrames { get; init; } = 256;

    /// <summary>Consecutive malformed inbound frames that close the connection. Default and ceiling 32, minimum 1.</summary>
    public int MaxConsecutiveMalformed { get; init; } = MaxMalformedCeiling;

    /// <summary>Terminals one connection may be subscribed to at once. Default 64, range 1 to 256.</summary>
    public int MaxSubscriptions { get; init; } = 64;

    /// <summary>Largest inbound frame in bytes; larger frames count as malformed. Default 64 KiB, range 1 KiB to 256 KiB.</summary>
    public int MaxInboundFrameBytes { get; init; } = 64 * 1024;

    /// <summary>Returns a copy with every value clamped into its allowed range.</summary>
    public TerminalConnectionOptions Normalize() => this with
    {
        OutboundQueueFrames = Math.Clamp(OutboundQueueFrames, 8, 4096),
        MaxConsecutiveMalformed = Math.Clamp(MaxConsecutiveMalformed, 1, MaxMalformedCeiling),
        MaxSubscriptions = Math.Clamp(MaxSubscriptions, 1, 256),
        MaxInboundFrameBytes = Math.Clamp(MaxInboundFrameBytes, 1024, 256 * 1024),
    };
}
