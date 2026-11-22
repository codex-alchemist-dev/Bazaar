using System.Globalization;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;

namespace Bazaar.Wire.Abstractions;

/// <summary>Constants and helpers shared by the client and server <c>quic</c> modules (doc 90 section 9).</summary>
public static class QuicWireSupport
{
    /// <summary>The ALPN protocol id, as text.</summary>
    public const string Alpn = "bazaar-wire/1";

    /// <summary>The ALPN protocol as <see cref="SslApplicationProtocol"/>; the one place both sides build it from <see cref="Alpn"/>.</summary>
    public static SslApplicationProtocol ApplicationProtocol { get; } = new(Alpn);

    /// <summary>A fresh ALPN list for <c>SslClientAuthenticationOptions</c>, <c>SslServerAuthenticationOptions</c> and <c>QuicListenerOptions</c>; the server and the client both take it from here.</summary>
    public static List<SslApplicationProtocol> CreateApplicationProtocols() => [ApplicationProtocol];

    /// <summary>Maps a QUIC exception to the close code the peer sent as its application error code (same number), or null when it is not a peer close.</summary>
    public static int? CloseCodeFromException(Exception exception)
    {
#pragma warning disable CA1416 // Only reached with a QuicException, which exists only where System.Net.Quic runs.
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is QuicException { ApplicationErrorCode: { } code } && code is >= 0 and <= int.MaxValue) return (int)code;
        return exception is QuicException { QuicError: QuicError.ConnectionAborted or QuicError.StreamAborted or QuicError.ConnectionIdle or QuicError.ConnectionRefused } ? 0 : null;
#pragma warning restore CA1416
    }

#pragma warning disable CA1416 // Diagnostics only read QUIC members on objects that already exist, so System.Net.Quic is present.
    /// <summary>Marker that starts the diagnostics text the connector and listener append to a failure message; tests and log greps can look for it.</summary>
    public const string DiagnosticsMarker = "[bazaar-quic diagnostics]";

    /// <summary><see cref="Exception.Data"/> key that carries the same diagnostics text on every exception the quic modules annotate.</summary>
    public const string DiagnosticsDataKey = "bazaar.quic.diagnostics";

    /// <summary>
    /// Builds the diagnostics text for a failed connect or accept: operation, endpoint, ALPN offered, ALPN negotiated (when the
    /// connection got far enough to know it), and every exception in the chain with its QUIC error, transport and application codes.
    /// </summary>
    public static string DescribeFailure(string operation, string? endpoint, IEnumerable<SslApplicationProtocol> offered, QuicConnection? connection, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(exception);
        var text = new StringBuilder(DiagnosticsMarker);
        text.Append(" operation=").Append(operation);
        text.Append(" endpoint=").Append(endpoint ?? "(none)");
        text.Append(" alpn-offered=[").AppendJoin(", ", offered.Select(static p => p.ToString())).Append(']');
        text.Append(" alpn-negotiated=").Append(NegotiatedOrNote(connection));
        text.Append(" chain=");
        var depth = 0;
        for (var current = exception; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            if (depth > 0) text.Append(" -> ");
            text.Append(current.GetType().Name);
            if (current is QuicException quic)
            {
                text.Append(CultureInfo.InvariantCulture, $"(QuicError={quic.QuicError}, TransportErrorCode={Code(quic.TransportErrorCode)}, ApplicationErrorCode={Code(quic.ApplicationErrorCode)})");
            }

            text.Append(CultureInfo.InvariantCulture, $"(HResult=0x{current.HResult:X8}): ").Append(current.Message);
        }

        return text.ToString();

        static string Code(long? code) => code is { } value ? value.ToString(CultureInfo.InvariantCulture) : "none";
    }

    /// <summary>
    /// Returns the exception to throw for <paramref name="original"/> with <paramref name="diagnostics"/> in its message.
    /// <see cref="AuthenticationException"/> is rebuilt as the same type (original as inner exception), so <c>catch</c> blocks behave as before.
    /// <see cref="QuicException"/> has no public constructor, so it and every other type are returned unchanged with the text in
    /// <see cref="Exception.Data"/> only (the connector and listener also log it through their diagnostics paths). Cancellation is never wrapped.
    /// </summary>
    public static Exception WithDiagnostics(Exception original, string diagnostics)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (original is OperationCanceledException) return original;
        Exception result = original switch
        {
            AuthenticationException => new AuthenticationException($"{original.Message} {diagnostics}", original),
            _ => original,
        };
        result.Data[DiagnosticsDataKey] = diagnostics;
        return result;
    }

    private static string NegotiatedOrNote(QuicConnection? connection)
    {
        if (connection is null) return "(no connection object: the handshake failed before one existed)";
        try
        {
            var negotiated = connection.NegotiatedApplicationProtocol;
            return negotiated.Protocol.IsEmpty ? "(none)" : negotiated.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or QuicException)
        {
            return $"(unavailable: {ex.GetType().Name})";
        }
    }

#pragma warning restore CA1416

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
