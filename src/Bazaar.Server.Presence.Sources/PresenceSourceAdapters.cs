using System.Collections.Concurrent;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.PsychWard.Abstractions;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Sources;

/// <summary>
/// Availability transitions to presence (doc 09, doc 23). The state maps one to one. The THROTTLED and COVERED overlays travel as
/// their own flag events, sent only when the overlay differs from the last one this adapter saw for the agent, so a transition that
/// leaves them alone adds no extra row revision.
/// </summary>
public sealed class AvailabilityPresenceSource(IActivityProjector projector) : IAvailabilityTransitionSink
{
    private readonly ConcurrentDictionary<Guid, (bool Throttled, bool Covered)> _overlays = new();

    /// <inheritdoc />
    public async ValueTask OnTransitionAsync(Guid agentId, AvailabilityTransition transition, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!transition.Applied)
        {
            return;
        }

        var snapshot = transition.Snapshot;
        await projector.OnEventAsync(ActivityEvent.AvailabilityChanged(agentId, at, Map(snapshot.State)), ct).ConfigureAwait(false);

        var throttled = snapshot.Overlays.OfType<AvailabilityOverlay.Throttled>().Any();
        var covered = snapshot.Overlays.OfType<AvailabilityOverlay.Covered>().Any();
        var before = _overlays.GetValueOrDefault(agentId);
        _overlays[agentId] = (throttled, covered);
        if (throttled != before.Throttled)
        {
            await projector.OnEventAsync(ActivityEvent.Flag(agentId, at, ActivityEventKind.QuotaThrottle, throttled), ct).ConfigureAwait(false);
        }

        if (covered != before.Covered)
        {
            await projector.OnEventAsync(ActivityEvent.Flag(agentId, at, ActivityEventKind.Covered, covered), ct).ConfigureAwait(false);
        }
    }

    internal static PresenceAvailability Map(AvailabilityMachineState state) => state switch
    {
        AvailabilityMachineState.OnShift => PresenceAvailability.OnShift,
        AvailabilityMachineState.Draining => PresenceAvailability.Draining,
        AvailabilityMachineState.Break => PresenceAvailability.Break,
        AvailabilityMachineState.Off => PresenceAvailability.Off,
        AvailabilityMachineState.Vacation => PresenceAvailability.Vacation,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}

/// <summary>
/// Turn and tool lifecycle to presence (doc 06, doc 23). A started turn, a model call and a finished tool (the next step is a model
/// call) show as a model call; a tool shows as a tool of its class; the end of the turn is idle. The GC kind maps to the GC context:
/// council and vote GCs to council-or-vote, a project GC of another project than the agent's to other-project, other project GCs to
/// project, anything else to other.
/// </summary>
public sealed class TurnPresenceSource(IActivityProjector projector, IPresenceToolClassifier classifier) : ITurnActivitySink
{
    /// <inheritdoc />
    public ValueTask OnTurnActivityAsync(TurnActivity activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var gc = MapGc(activity);
        ActivityEvent e = activity.Kind switch
        {
            TurnActivityKind.TurnStarted or TurnActivityKind.ModelCallStarted or TurnActivityKind.ToolFinished =>
                Turn(activity, PresenceActivityKind.ModelCall, PresenceToolClass.None, gc),
            TurnActivityKind.ToolStarted =>
                Turn(activity, PresenceActivityKind.Tool, classifier.Classify(activity.ToolName), gc),
            TurnActivityKind.Drafting => Turn(activity, PresenceActivityKind.Drafting, PresenceToolClass.None, gc),
            TurnActivityKind.ApprovalWaitStarted => ActivityEvent.Flag(activity.AgentId, activity.At, ActivityEventKind.ApprovalWait, true),
            TurnActivityKind.ApprovalWaitEnded => ActivityEvent.Flag(activity.AgentId, activity.At, ActivityEventKind.ApprovalWait, false),
            TurnActivityKind.TurnEnded => ActivityEvent.TurnChanged(activity.AgentId, activity.At, PresenceActivityKind.Idle, turnId: activity.TurnId),
            _ => throw new ArgumentOutOfRangeException(nameof(activity), activity.Kind, null),
        };
        return projector.OnEventAsync(e, ct);
    }

    internal static PresenceGcContext MapGc(TurnActivity a) => a.GcKind switch
    {
        TurnGcKind.Council or TurnGcKind.Vote => PresenceGcContext.CouncilOrVote,
        TurnGcKind.Project when a.GcProjectId is { } gcProject && a.AgentProjectId is { } own
            && !string.Equals(gcProject, own, StringComparison.Ordinal) => PresenceGcContext.OtherProjectGc,
        TurnGcKind.Project => PresenceGcContext.ProjectGc,
        _ => PresenceGcContext.Other,
    };

    private static ActivityEvent Turn(TurnActivity a, PresenceActivityKind kind, PresenceToolClass toolClass, PresenceGcContext gc) =>
        ActivityEvent.TurnChanged(a.AgentId, a.At, kind, toolClass, gc, a.GcId, a.TurnId, a.Label);
}

/// <summary>
/// Gateway quota to the THROTTLED overlay (doc 05, doc 09). A rate limit or an exhausted daily allowance with no allowed candidate
/// left turns it on; capacity coming back turns it off. A spend cap with no source left is a vacation (the availability machine owns
/// that, through <see cref="AvailabilityPresenceSource"/>), and a provider that is down is not a throttle, so neither sets the overlay.
/// A capacity event for an agent id that is not a GUID is ignored.
/// </summary>
public sealed class QuotaPresenceSource(IActivityProjector projector, TimeProvider time) : IQuotaCapacitySink
{
    /// <inheritdoc />
    public ValueTask OnCapacityChangedAsync(AgentCapacityChanged capacity, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        if (!Guid.TryParse(capacity.AgentId, out var agentId))
        {
            return ValueTask.CompletedTask;
        }

        if (capacity.Available)
        {
            return projector.OnEventAsync(ActivityEvent.Flag(agentId, at, ActivityEventKind.QuotaThrottle, false), ct);
        }

        return capacity.Reason is QuotaState.RateLimited or QuotaState.DailyExhausted
            ? projector.OnEventAsync(ActivityEvent.Flag(agentId, at, ActivityEventKind.QuotaThrottle, true), ct)
            : ValueTask.CompletedTask;
    }

    /// <summary>
    /// Adapts the gateway's synchronous <c>IGatewayEventSink.Publish</c> shape: the host's composite sink hands each event here. It
    /// stamps the clock's time and forwards without waiting; a failure is dropped, since presence is state the next event repairs.
    /// </summary>
    public void Publish(GatewayQuotaEvent e)
    {
        if (e is AgentCapacityChanged capacity)
        {
            _ = ForwardAsync(capacity, time.GetUtcNow());
        }
    }

    private async Task ForwardAsync(AgentCapacityChanged capacity, DateTimeOffset at)
    {
        try
        {
            await OnCapacityChangedAsync(capacity, at, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort, as the projector's own publish: the next event for the agent sets the flag again.
        }
    }
}

/// <summary>Kanban waits to presence (doc 12, doc 23): waiting on a prerequisite is the Waiting activity.</summary>
public sealed class KanbanPresenceSource(IActivityProjector projector) : IKanbanWaitSink
{
    /// <inheritdoc />
    public ValueTask OnWaitChangedAsync(Guid agentId, DateTimeOffset at, bool waiting, CancellationToken ct) =>
        projector.OnEventAsync(ActivityEvent.Flag(agentId, at, ActivityEventKind.KanbanWait, waiting), ct);
}

/// <summary>Psych Ward stays to the WARD overlay (doc 26, doc 23): on at admission, off at discharge.</summary>
public sealed class WardPresenceSource(IActivityProjector projector) : IWardStateSink
{
    /// <inheritdoc />
    public ValueTask OnStayChangedAsync(WardStay stay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stay);
        return projector.OnEventAsync(
            ActivityEvent.Flag(stay.AgentId, stay.DischargedAt ?? stay.AdmittedAt, ActivityEventKind.Ward, stay.IsAdmitted),
            ct);
    }
}
