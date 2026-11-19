using Microsoft.Extensions.Logging;

namespace Bazaar.Conduit.Ssh;

/// <summary>Compile-time logging delegates for the ssh module. Never log the identity file path or contents, or ssh stderr.</summary>
internal static partial class SshLog
{
    [LoggerMessage(Level = LogLevel.Warning, EventId = 1, Message = "SSH reverse tunnel exited with code {ExitCode}; retrying in {DelaySeconds} seconds")]
    public static partial void ProcessExited(ILogger logger, int exitCode, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 2, Message = "SSH reverse tunnel failed to start: {Reason}")]
    public static partial void LaunchFailed(ILogger logger, string reason);
}
