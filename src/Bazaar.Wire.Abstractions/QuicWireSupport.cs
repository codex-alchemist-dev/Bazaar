using System.Net.Quic;

namespace Bazaar.Wire.Abstractions;

/// <summary>Constants and helpers shared by the client and server <c>quic</c> modules (doc 90 section 9).</summary>
public static class QuicWireSupport
{
    /// <summary>The ALPN protocol id, as text.</summary>
    public const string Alpn = "bazaar-wire/1";

    /// <summary>Maps a QUIC exception to the close code the peer sent as its application error code (same number), or null when it is not a peer close.</summary>
    public static int? CloseCodeFromException(Exception exception)
    {
#pragma warning disable CA1416 // Only reached with a QuicException, which exists only where System.Net.Quic runs.
#pragma warning disable CA1416 // Only reached with a QuicException, which exists only where System.Net.Quic runs.
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is QuicException { ApplicationErrorCode: { } code } && code is >= 0 and <= int.MaxValue) return (int)code;
        return exception is QuicException { QuicError: QuicError.ConnectionAborted or QuicError.StreamAborted or QuicError.ConnectionIdle or QuicError.ConnectionRefused } ? 0 : null;
#pragma warning restore CA1416
    }

    /// <summary>Splits <c>host:port</c> (an IPv6 literal may be bracketed).</summary>
    /// <exception cref="FormatException">The endpoint is not <c>host:port</c> with a valid port.</exception>
    public static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint.AsSpan(colon + 1), out var port) || port is < 1 or > 65535) throw new FormatException("Endpoint must be host:port.");
        return (endpoint[..colon].Trim('[', ']'), port);
    }
}
