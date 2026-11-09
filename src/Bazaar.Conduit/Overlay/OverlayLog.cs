using Microsoft.Extensions.Logging;

namespace Bazaar.Conduit;

/// <summary>Compile-time logging delegates for the overlay modules. Never pass addresses or secrets as arguments.</summary>
internal static partial class OverlayLog
{
    [LoggerMessage(Level = LogLevel.Warning, EventId = 1, Message = "Overlay module {ModuleId} did not start: {Reason}")]
    public static partial void DidNotStart(ILogger logger, string moduleId, string reason);
}
