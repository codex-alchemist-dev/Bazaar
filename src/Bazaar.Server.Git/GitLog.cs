using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Git;

/// <summary>Log messages for the forge-facing side. Only types and ids are logged, never forge response text or a token.</summary>
internal static partial class GitLog
{
    [LoggerMessage(EventId = 5201, Level = LogLevel.Error, Message = "Could not audit {Kind} for {Provider}#{Number}.")]
    public static partial void AuditFailed(ILogger logger, Exception exception, string kind, string provider, long number);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Warning, Message = "Forge list failed for {Provider}; serving the cached page ({Error}).")]
    public static partial void ServingStalePage(ILogger logger, string provider, string error);

    [LoggerMessage(EventId = 5203, Level = LogLevel.Warning, Message = "Forge read failed ({Error}).")]
    public static partial void ForgeReadFailed(ILogger logger, string error);
}
