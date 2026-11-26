using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Agents.Scheduling;

/// <summary>Hosted service that writes one line at start that says whether the wake loop is on and whether that is the default or the operator's choice.</summary>
public sealed partial class WakeLoopStateLogger(ILogger<WakeLoopStateLogger> logger, bool enabled, bool enabledByDefault = false) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (enabled)
            LogOn(logger, enabledByDefault ? "default" : "operator setting");
        else
            LogOff(logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The wake loop is on by default but cannot run turns here, so it stays idle: {Reason}")]
    public static partial void LogIdle(ILogger logger, Exception exception, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent wake loop is on ({Source}). Every wake is checked against the kill switch and the loop guard.")]
    private static partial void LogOn(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent wake loop is off (Bazaar:AgentTurns:Scheduler:Enabled=false, set by the operator). No agent wakes on a card or a mention.")]
    private static partial void LogOff(ILogger logger);
}
