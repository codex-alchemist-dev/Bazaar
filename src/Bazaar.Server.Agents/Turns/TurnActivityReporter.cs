using Bazaar.Server.Presence.Sources.Abstractions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// Reports turn and tool lifecycle events to the presence port (doc 06, docs/design/23 "Event sources"). The sink is optional: with
/// none, every call is a no-op, so the runtime works with no presence module registered. A sink failure never fails the turn; it is
/// logged at debug and the turn carries on. An external cancel still reaches the caller.
/// </summary>
public sealed class TurnActivityReporter(ITurnActivitySink? sink, TimeProvider? time = null, ILogger? logger = null,
    IGcKindLookup? gcKinds = null, IAgentProjectLookup? agentProjects = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Reports one event for the turn. <paramref name="gcId"/> is the trigger GC text, when it is a GUID.</summary>
    public async ValueTask ReportAsync(
        Guid agentId, Guid turnId, string? gcId, TurnActivityKind kind, string? toolName = null, CancellationToken ct = default)
    {
        if (sink is null)
        {
            return;
        }

        try
        {
            Guid? gc = Guid.TryParse(gcId, out var parsed) ? parsed : null;
            var gcInfo = GcKindInfo.Other;
            string? agentProject = null;
            try
            {
                if (gc is { } id && gcKinds is not null)
                    gcInfo = await gcKinds.LookupAsync(id, ct).ConfigureAwait(false);
                if (agentProjects is not null)
                    agentProject = await agentProjects.GetCurrentProjectAsync(agentId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // An unknown GC or project reads as Other and none; the event still goes out.
                gcInfo = GcKindInfo.Other;
            }

            var activity = new TurnActivity(agentId, _time.GetUtcNow(), kind, turnId, gc, gcInfo.Kind, gcInfo.ProjectId, agentProject, toolName);
            await sink.OnTurnActivityAsync(activity, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (logger is not null)
            {
                TurnActivityLog.SinkFailed(logger, kind, ex);
            }
        }
    }
}

internal static partial class TurnActivityLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "The turn activity sink failed on {Kind}; the turn carries on.")]
    public static partial void SinkFailed(ILogger logger, TurnActivityKind kind, Exception exception);
}
