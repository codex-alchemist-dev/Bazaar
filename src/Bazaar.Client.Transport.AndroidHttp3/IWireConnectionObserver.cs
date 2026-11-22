namespace Bazaar.Client.Transport.AndroidHttp3;

/// <summary>
/// Makes what a long-lived Android connection goes through observable (doc 90 section 9: behaviour through Doze and
/// network handover is measured in the spike). The module reports; it never decides a number or a retry policy.
/// Implementations must not throw and must be quick.
/// </summary>
public interface IWireConnectionObserver
{
    /// <summary>A connection opened and Cronet negotiated <paramref name="protocol"/>.</summary>
    void OnNegotiated(string protocol);

    /// <summary>The network changed under, suspended or resumed an open connection.</summary>
    void OnNetworkEvent(CronetNetworkEvent networkEvent);

    /// <summary>The connection is lost and the connection layer should reconnect.</summary>
    void OnReconnectRequired(ReconnectReason reason, CronetNetworkEvent? lastNetworkEvent);

    /// <summary>A transport step failed and the ladder moves on to the next.</summary>
    void OnFallback(string fromTransport, string reason);
}

/// <summary>Why a reconnect is needed.</summary>
public enum ReconnectReason
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

    /// <inheritdoc />
    public void OnNegotiated(string protocol)
    {
    }

    /// <inheritdoc />
    public void OnNetworkEvent(CronetNetworkEvent networkEvent)
    {
    }

    /// <inheritdoc />
    public void OnReconnectRequired(ReconnectReason reason, CronetNetworkEvent? lastNetworkEvent)
    {
    }

    /// <inheritdoc />
    public void OnFallback(string fromTransport, string reason)
    {
    }
}
