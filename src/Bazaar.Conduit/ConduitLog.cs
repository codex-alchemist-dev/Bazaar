using Microsoft.Extensions.Logging;

namespace Bazaar.Conduit;

/// <summary>Compile-time logging delegates for the transport layer. Never pass secrets as arguments.</summary>
internal static partial class ConduitLog
{
    [LoggerMessage(Level = LogLevel.Warning, EventId = 1, Message = "Transport module {ModuleId} failed to start")]
    public static partial void StartFailed(ILogger logger, string moduleId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 2, Message = "Transport module {ModuleId} failed to stop cleanly")]
    public static partial void StopFailed(ILogger logger, string moduleId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, EventId = 3, Message = "Dropped offer from {ModuleId}: {Reason}")]
    public static partial void OfferDropped(ILogger logger, string moduleId, string reason);
}
