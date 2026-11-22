namespace Bazaar.Wire.Abstractions;

/// <summary>
/// Observes a wire connection (doc 90 section 9a). Every member has a default no-op body, so an observer overrides
/// only what it cares about. Implementations must not throw and must be quick.
/// <list type="bullet">
/// <item>Server side: <see cref="OnClosedAsync"/>, called once per connection when it ends, on any transport.</item>
/// <item>Client side (used by the Android Cronet transport): negotiation, network events, reconnect and fallback
/// reports. The observer reports; the module never decides a number or a retry policy.</item>
/// </list>
/// </summary>
public interface IWireConnectionObserver
{
    /// <summary>
    /// Runs after the connection closes for any reason (clean, error, timeout, revoke) and after its in-flight
    /// handlers finish. A throwing or slow observer (over 5 s) is logged and skipped by the connection layer.
    /// </summary>
    /// <param name="connectionId">The id the connection layer assigned; the same on every transport.</param>
    /// <param name="cancellationToken">Cancelled when the close budget runs out.</param>
    ValueTask OnClosedAsync(Guid connectionId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>A client connection opened and the platform stack negotiated <paramref name="protocol"/> (for example <c>h3</c>).</summary>
    void OnNegotiated(string protocol)
    {
    }

    /// <summary>The network changed under, suspended or resumed an open client connection.</summary>
    void OnNetworkEvent(WireNetworkEvent networkEvent)
    {
    }

    /// <summary>A client connection is lost and the connection layer should reconnect.</summary>
    void OnReconnectRequired(WireReconnectReason reason, WireNetworkEvent? lastNetworkEvent)
    {
    }

    /// <summary>A transport step failed and the connect ladder moves on to the next.</summary>
    void OnFallback(string fromTransport, string reason)
    {
    }
}

/// <summary>Kinds of network events a client connection reports. Their effect on a long-lived stream is measured in the spike, not assumed.</summary>
public enum WireNetworkEventKind
{
    /// <summary>The device moved to another network (Wi-Fi to mobile data and so on).</summary>
    Handover,

    /// <summary>The device suspended the app's network (Doze, no connectivity).</summary>
    Suspended,

    /// <summary>The network is usable again.</summary>
    Resumed,
}

/// <summary>A network event on a client connection.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="At">When the platform binding saw it.</param>
/// <param name="Detail">Binding-specific detail for diagnostics; never a secret.</param>
public sealed record WireNetworkEvent(WireNetworkEventKind Kind, DateTimeOffset At, string? Detail = null);

/// <summary>Why a client connection needs a reconnect.</summary>
public enum WireReconnectReason
{
    /// <summary>A read or write failed.</summary>
    StreamFailed,

    /// <summary>The stream ended without trailers.</summary>
    StreamCut,
}

/// <summary>An observer that does nothing.</summary>
public sealed class NullWireConnectionObserver : IWireConnectionObserver
{
    /// <summary>The shared instance.</summary>
    public static NullWireConnectionObserver Instance { get; } = new();
}
