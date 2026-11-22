namespace Bazaar.Client.Transport.AndroidHttp3.Android.Head;

/// <summary>The Java failure family.</summary>
internal enum CronetFailureKind
{
    /// <summary>A plain <c>CronetException</c> (for example the engine was shut down).</summary>
    Cronet,

    /// <summary>A <c>NetworkException</c>.</summary>
    Network,

    /// <summary>A <c>QuicException</c> (a <c>NetworkException</c> subclass).</summary>
    Quic,
}

/// <summary>What Cronet's <c>CronetException</c> carries, without the Java type.</summary>
/// <param name="Kind">The Java exception kind.</param>
/// <param name="ErrorCode"><c>NetworkException.getErrorCode()</c>, or -1 when absent.</param>
/// <param name="CronetInternalErrorCode"><c>NetworkException.getCronetInternalErrorCode()</c> (a net error, negative), or 0 when absent.</param>
/// <param name="Message">The Java message.</param>
internal sealed record CronetFailureInfo(CronetFailureKind Kind, int ErrorCode, int CronetInternalErrorCode, string Message);

/// <summary>Maps Cronet failures to the <see cref="IOException"/> the stream adapter documents for <c>onFailed</c>.</summary>
internal static class CronetFailure
{
    // org.chromium.net.NetworkException.ERROR_* values.
    private const int HostLookup = 1;
    private const int Timed = 4;
    private const int ConnectionClosed = 5;
    private const int ConnectionReset = 6;
    private const int ConnectionRefused = 7;
    private const int Disconnected = 9;

    /// <summary>The exception for <paramref name="info"/>. The message names the Java error code; it never carries a URL or header.</summary>
    public static IOException ToException(CronetFailureInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var label = info.Kind switch
        {
            CronetFailureKind.Quic => "QUIC",
            CronetFailureKind.Network => "network",
            _ => "engine",
        };
        var reason = info.Kind == CronetFailureKind.Cronet ? string.Empty : $" ({Describe(info.ErrorCode)}, net error {info.CronetInternalErrorCode})";
        return new IOException($"Cronet {label} failure{reason}: {info.Message}");
    }

    /// <summary>A short stable name for a <c>NetworkException</c> error code.</summary>
    public static string Describe(int errorCode) => errorCode switch
    {
        HostLookup => "host lookup failed",
        Timed => "timed out",
        ConnectionClosed => "connection closed",
        ConnectionReset => "connection reset",
        ConnectionRefused => "connection refused",
        Disconnected => "internet disconnected",
        _ => $"error {errorCode}",
    };
}
