namespace Bazaar.Client.Transport.AndroidHttp3;

/// <summary>This transport cannot serve the connection (no Cronet, HTTP/3 blocked, odd HTTP answer); try the next transport on the ladder.</summary>
public sealed class WireFallbackException : IOException
{
    /// <summary>Creates the exception.</summary>
    public WireFallbackException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public WireFallbackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The server answered and refused (bad credential, no common codec). Another transport would get the same answer, so the ladder stops.</summary>
public sealed class WireConnectRefusedException : IOException
{
    /// <summary>Creates the exception.</summary>
    public WireConnectRefusedException(string message, int closeCode)
        : base(message) => CloseCode = closeCode;

    /// <summary>The wire close code, such as 4401.</summary>
    public int CloseCode { get; }
}
