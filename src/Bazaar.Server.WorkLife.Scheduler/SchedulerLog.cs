using Microsoft.Extensions.Logging;

namespace Bazaar.Server.WorkLife.Scheduler;

/// <summary>Source-generated log messages for the scheduler.</summary>
internal static partial class SchedulerLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Shift scheduler failed for agent {AgentId}; it is tried again next tick.")]
    public static partial void AgentFailed(ILogger logger, Exception ex, Guid agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Shift scheduler event {EventName} for agent {AgentId} not applied: {Reason}")]
    public static partial void NotApplied(ILogger logger, string eventName, Guid agentId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Presence sink failed for agent {AgentId}; the transition stands.")]
    public static partial void SinkFailed(ILogger logger, Exception ex, Guid agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shift scheduler tick failed; the next tick runs on schedule.")]
    public static partial void TickFailed(ILogger logger, Exception ex);
}
