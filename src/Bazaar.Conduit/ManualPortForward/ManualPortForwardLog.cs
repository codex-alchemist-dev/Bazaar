using Microsoft.Extensions.Logging;

namespace Bazaar.Conduit;

/// <summary>Compile-time logging for the manual port forward module. Never log the probe URL or host.</summary>
internal static partial class ManualPortForwardLog
{
    [LoggerMessage(Level = LogLevel.Warning, EventId = 100, Message = "Manual port forward did not verify the public address: {Reason}")]
    public static partial void NotVerified(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 101, Message = "Manual port forward settings rejected: {Reason}")]
    public static partial void SettingsRejected(ILogger logger, string reason);
}
