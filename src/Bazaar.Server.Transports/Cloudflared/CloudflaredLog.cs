using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Transports.Cloudflared;

/// <summary>Compile-time log messages for the cloudflared module. Never log the tunnel token or process output.</summary>
internal static partial class CloudflaredLog
{
    [LoggerMessage(Level = LogLevel.Warning, EventId = 100, Message = "cloudflared exited with code {ExitCode}; restarting in {DelaySeconds} seconds")]
    public static partial void ProcessExited(ILogger logger, int exitCode, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 101, Message = "cloudflared could not start: {Reason}")]
    public static partial void LaunchFailed(ILogger logger, string reason);
}
