namespace Bazaar.Wire.Abstractions;

/// <summary>
/// Result of a transport-level credential check. The shape mirrors the server's connection authenticator
/// (credential in, principal out) without referencing it, so every transport module shares one hook (doc 90 section 9).
/// </summary>
/// <param name="Ok">True when the credential is valid.</param>
/// <param name="Subject">Opaque principal reference the connection layer understands; null when not <paramref name="Ok"/>.</param>
/// <param name="DeviceId">Server-assigned device id, or null to keep the one the client sends in <c>hello</c>.</param>
/// <param name="ExpiresAt">When the credential stops being valid (the connection then closes with 4401), or null.</param>
/// <param name="Reason">Why it failed; logged, never sent to the client.</param>
public sealed record WireAuthResult(bool Ok, string? Subject, string? DeviceId, DateTimeOffset? ExpiresAt, string? Reason)
{
    /// <summary>A successful result.</summary>
    public static WireAuthResult Success(string subject, string? deviceId = null, DateTimeOffset? expiresAt = null) =>
        new(true, subject, deviceId, expiresAt, null);

    /// <summary>A failed result.</summary>
    public static WireAuthResult Fail(string reason) => new(false, null, null, null, reason);
}

/// <summary>
/// Checks the credential a transport read from its own metadata (WebSocket <c>Authorization</c> header, gRPC
/// <c>authorization</c> metadata). The server's connection layer adapts its authenticator to this interface.
/// </summary>
public interface IWireTransportAuthenticator
{
    /// <summary>Checks <paramref name="credential"/>; null means none was presented.</summary>
    /// <param name="credential">The bearer credential with any scheme prefix removed, or null.</param>
    /// <param name="transport">Transport id, such as <c>grpc</c>.</param>
    /// <param name="deviceId">The device id the client announced in transport metadata, or null.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<WireAuthResult> AuthenticateAsync(string? credential, string transport, string? deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// The connection hand-off: a transport module calls this once per accepted and authenticated connection. The
/// connection layer runs the wire protocol on it (hello, welcome, dispatch) and returns when the connection is over.
/// </summary>
public interface IWireConnectionHandoff
{
    /// <summary>Runs the connection to its end.</summary>
    ValueTask HandleAsync(IWireConnection connection, WireAuthResult authentication, CancellationToken cancellationToken);
}

/// <summary>A connection the connection layer can close with a wire close code (such as 4401) that the peer sees.</summary>
public interface ICloseableWireConnection : IWireConnection
{
    /// <summary>Marks the connection closed with <paramref name="code"/>; the peer receives it as <see cref="WireReceive.CloseCode"/>.</summary>
    ValueTask CloseAsync(int code, CancellationToken cancellationToken);
}
