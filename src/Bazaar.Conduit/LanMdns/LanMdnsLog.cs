using Microsoft.Extensions.Logging;

namespace Bazaar.Conduit.LanMdns;

/// <summary>Compile-time logging delegates for the lan-mdns module. Never log addresses of tunnels, keys or secrets.</summary>
internal static partial class LanMdnsLog
{
    [LoggerMessage(Level = LogLevel.Warning, EventId = 100, Message = "LAN discovery found no LAN IPv4 address to publish")]
    public static partial void NoLanAddress(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 101, Message = "LAN discovery could not bind the mDNS socket")]
    public static partial void BindFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 102, Message = "LAN discovery could not list network interfaces")]
    public static partial void AddressLookupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 103, Message = "LAN discovery could not send its announcement or goodbye")]
    public static partial void SendFailed(ILogger logger, Exception exception);
}
