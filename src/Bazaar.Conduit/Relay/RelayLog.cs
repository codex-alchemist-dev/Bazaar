using Microsoft.Extensions.Logging;

namespace Bazaar.Conduit.Relay;

/// <summary>Compile-time logging for the relay module. Never log the relay URL, the room or any frame content.</summary>
internal static partial class RelayLog
{
    [LoggerMessage(Level = LogLevel.Warning, EventId = 200, Message = "Relay settings rejected: {Reason}")]
    public static partial void SettingsRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 201, Message = "Relay failed to start: {ExceptionType}")]
    public static partial void LaunchFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 202, Message = "Relay connection dropped; reconnecting with backoff")]
    public static partial void Dropped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 203, Message = "Relay reconnect attempt failed: {ExceptionType}")]
    public static partial void ReconnectFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 204, Message = "Relay socket failed: {ExceptionType}")]
    public static partial void SocketFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 205, Message = "Relay did not close cleanly on stop: {ExceptionType}")]
    public static partial void StopFailed(ILogger logger, string exceptionType);
}
