using Bazaar.Contracts.Engines;

namespace Bazaar.Server.EngineDispatch;

/// <summary>
/// Finds the device a relay request goes to: the owner's connected Windows desktop (doc 20, doc 28). Backed by the wire kernel's
/// connection registry, which is outside this module. Failures throw; the broker then reports <see cref="EngineRelayStatus.Unavailable"/>.
/// </summary>
public interface IRelayTargetDirectory
{
    /// <summary>The device id of the connected target, or null when none is connected. Never a queue: null fails the call at once.</summary>
    ValueTask<string?> FindTargetDeviceAsync(CancellationToken ct);
}

/// <summary>Sends a relay event to one device (an <c>evt</c> on <see cref="EngineRelayWire.Stream"/>). Throws when the device is gone.</summary>
public interface IRelayPublisher
{
    /// <summary>Publishes the event to the device.</summary>
    ValueTask PublishAsync(string deviceId, EngineRelayRequestEvent relayEvent, CancellationToken ct);
}

/// <summary>How a relay call ended.</summary>
public enum EngineRelayStatus
{
    /// <summary>The device answered; see the result (its <c>Ok</c> may still be false).</summary>
    Completed,

    /// <summary>The dispatcher denied the call; nothing was published.</summary>
    Denied,

    /// <summary>No connected target, publish failed, or the connection closed while waiting.</summary>
    Unavailable,

    /// <summary>The device did not answer before the deadline.</summary>
    Timeout,
}

/// <summary>The result of <c>EngineRelayBroker.RelayAsync</c>.</summary>
/// <param name="Status">How the call ended.</param>
/// <param name="Result">The device's answer when <paramref name="Status"/> is <see cref="EngineRelayStatus.Completed"/>.</param>
/// <param name="DenyReason">The dispatcher's reason when <paramref name="Status"/> is <see cref="EngineRelayStatus.Denied"/>.</param>
/// <param name="Detail">A short, content-free reason for logs and the capability's <c>unavailable</c> text.</param>
public sealed record EngineRelayOutcome(EngineRelayStatus Status, EngineRelayCallResult? Result, EngineDispatchReason? DenyReason, string? Detail)
{
    public static EngineRelayOutcome Completed(EngineRelayCallResult result) => new(EngineRelayStatus.Completed, result, null, null);

    public static EngineRelayOutcome Denied(EngineDispatchReason reason) => new(EngineRelayStatus.Denied, null, reason, null);

    public static EngineRelayOutcome Unavailable(string detail) => new(EngineRelayStatus.Unavailable, null, null, detail);

    public static EngineRelayOutcome TimedOut() => new(EngineRelayStatus.Timeout, null, null, "the device did not answer before the deadline");
}

/// <summary>What <c>EngineRelayBroker.SubmitResult</c> did with an answer. Everything except <see cref="Accepted"/> is ignored (the wire answers <c>err</c>).</summary>
public enum EngineRelaySubmitStatus
{
    /// <summary>The answer completed a pending relay.</summary>
    Accepted,

    /// <summary>The relay id is unknown, expired, or already answered.</summary>
    Unknown,

    /// <summary>The relay went to a different device. The pending relay is left untouched.</summary>
    ForeignDevice,

    /// <summary>The answer's grant id does not echo the request's. The pending relay is left untouched.</summary>
    GrantMismatch,
}

/// <summary>Relay settings (doc 20, settings table).</summary>
public sealed class EngineRelayOptions
{
    /// <summary>Upper bound for the timeout; a larger configured value is clamped.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary><c>engine.relay.timeout_seconds</c>: how long the server waits for the device. A non-positive value falls back to the default.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(EngineRelayWire.DefaultTimeoutSeconds);

    /// <summary>The timeout actually used: default when unset or non-positive, never above <see cref="MaxTimeout"/>.</summary>
    public TimeSpan EffectiveTimeout =>
        Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(EngineRelayWire.DefaultTimeoutSeconds) : Timeout > MaxTimeout ? MaxTimeout : Timeout;
}
