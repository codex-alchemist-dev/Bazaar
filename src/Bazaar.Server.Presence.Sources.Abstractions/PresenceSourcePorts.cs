using Bazaar.Contracts.Gateway;
using Bazaar.Server.PsychWard.Abstractions;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Sources.Abstractions;

// Ports the source modules call to feed presence (docs/design/23, "Event sources and subscription"). Each source module keeps its
// own model; the adapter in Bazaar.Server.Presence.Sources maps it to an ActivityEvent. A source needs no reference to presence
// beyond this project. Where a source has no observer seam today, the call site is named in the port's summary.

/// <summary>
/// Availability transitions (doc 09). Call site, not yet wired: wherever the work-life module applies a submitted
/// <c>AvailabilityMachine</c> event and keeps the result; call it with every transition whose <c>Applied</c> is true.
/// </summary>
public interface IAvailabilityTransitionSink
{
    /// <summary>Reports an applied transition. <paramref name="at"/> is the instant the transition took effect.</summary>
    ValueTask OnTransitionAsync(Guid agentId, AvailabilityTransition transition, DateTimeOffset at, CancellationToken ct);
}

/// <summary>What a turn or tool lifecycle event reports (doc 06).</summary>
public enum TurnActivityKind
{
    /// <summary>The turn was claimed and began.</summary>
    TurnStarted,

    /// <summary>A model call is in flight.</summary>
    ModelCallStarted,

    /// <summary>A tool call started. <see cref="TurnActivity.ToolName"/> names the tool.</summary>
    ToolStarted,

    /// <summary>A tool call finished; the turn continues with its next step.</summary>
    ToolFinished,

    /// <summary>The agent is writing its post.</summary>
    Drafting,

    /// <summary>The turn is waiting on an approval (the dispatcher returned <c>PendingApproval</c>).</summary>
    ApprovalWaitStarted,

    /// <summary>The approval was decided and the turn goes on.</summary>
    ApprovalWaitEnded,

    /// <summary>The turn ended, for any reason.</summary>
    TurnEnded,
}

/// <summary>The kind of GC a turn runs in, as the runtime knows it.</summary>
public enum TurnGcKind
{
    /// <summary>A project GC (a company or project channel).</summary>
    Project,

    /// <summary>A council GC.</summary>
    Council,

    /// <summary>A vote GC.</summary>
    Vote,

    /// <summary>A DM, a Thinking GC or anything else.</summary>
    Other,
}

/// <summary>What a GC is, for presence: its <see cref="TurnGcKind"/> and, for a project GC, the project it belongs to.</summary>
/// <param name="Kind">The kind. <see cref="TurnGcKind.Other"/> when unknown.</param>
/// <param name="ProjectId">The project slug, or null when the GC has none.</param>
public readonly record struct GcKindInfo(TurnGcKind Kind, string? ProjectId = null)
{
    /// <summary>The answer for an unknown GC.</summary>
    public static GcKindInfo Other => new(TurnGcKind.Other);
}

/// <summary>Says what kind of GC a turn runs in (doc 23, GC context). An implementation never throws for an unknown GC; it answers <see cref="GcKindInfo.Other"/>.</summary>
public interface IGcKindLookup
{
    /// <summary>Looks up the GC.</summary>
    ValueTask<GcKindInfo> LookupAsync(Guid gcId, CancellationToken ct);
}

/// <summary>
/// The project an agent is working on now, so a project GC of another project reads as "other project". Optional: the agent runtime
/// exposes no current project yet, so with no implementation registered the project-differs rule never fires.
/// </summary>
public interface IAgentProjectLookup
{
    /// <summary>The agent's current project slug, or null when it has none.</summary>
    ValueTask<string?> GetCurrentProjectAsync(Guid agentId, CancellationToken ct);
}

/// <summary>One turn or tool lifecycle event.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="At">When it happened (UTC).</param>
/// <param name="Kind">What happened.</param>
/// <param name="TurnId">The turn.</param>
/// <param name="GcId">The GC the turn runs in, when known.</param>
/// <param name="GcKind">The kind of that GC.</param>
/// <param name="GcProjectId">The project the GC belongs to, for a project GC.</param>
/// <param name="AgentProjectId">The project the agent is working on now; a project GC of another project reads as "other project".</param>
/// <param name="ToolName">The tool, for tool events. Classified into research, review or edit-or-build.</param>
/// <param name="Label">An optional short activity label. Presence scrubs and may drop it.</param>
public sealed record TurnActivity(
    Guid AgentId,
    DateTimeOffset At,
    TurnActivityKind Kind,
    Guid TurnId,
    Guid? GcId = null,
    TurnGcKind GcKind = TurnGcKind.Other,
    string? GcProjectId = null,
    string? AgentProjectId = null,
    string? ToolName = null,
    string? Label = null);

/// <summary>
/// Turn and tool lifecycle (doc 06). Call site, not yet wired: the turn runner (<c>ITurnRunner</c> implementation in
/// <c>Bazaar.Server.Agents</c>) at claim, before each model call and tool dispatch, after each tool result, before posting and in its
/// <c>finally</c>; the tool dispatcher for the approval wait.
/// </summary>
public interface ITurnActivitySink
{
    /// <summary>Reports one lifecycle event.</summary>
    ValueTask OnTurnActivityAsync(TurnActivity activity, CancellationToken ct);
}

/// <summary>
/// Quota and throttle (doc 05, doc 09). The gateway already reports <see cref="AgentCapacityChanged"/> through its
/// <c>IGatewayEventSink</c>; the host composes that sink with this one.
/// </summary>
public interface IQuotaCapacitySink
{
    /// <summary>Reports that the agent has, or lost, every allowed candidate.</summary>
    ValueTask OnCapacityChangedAsync(AgentCapacityChanged capacity, DateTimeOffset at, CancellationToken ct);
}

/// <summary>
/// Kanban waits (doc 12). Call site, not yet wired: the Kanban module (no module exists yet) when a card an agent works on becomes
/// blocked by, or is freed from, an unfinished prerequisite.
/// </summary>
public interface IKanbanWaitSink
{
    /// <summary>Reports that the agent started or stopped waiting on a prerequisite.</summary>
    ValueTask OnWaitChangedAsync(Guid agentId, DateTimeOffset at, bool waiting, CancellationToken ct);
}

/// <summary>
/// Psych Ward state (doc 26). Call site, not yet wired: <c>IPsychWardService</c> implementation in <c>Bazaar.Server.PsychWard</c>,
/// after it saves an admitted or discharged stay.
/// </summary>
public interface IWardStateSink
{
    /// <summary>Reports a saved stay: admitted while <see cref="WardStay.IsAdmitted"/>, otherwise discharged.</summary>
    ValueTask OnStayChangedAsync(WardStay stay, CancellationToken ct);
}

// Restart seed ports (docs/design/23, "Restart"). The presence table is not a seed source (it is what is being rebuilt), so the
// seed reads the modules that own the truth. No implementation of the three readers exists yet; see each summary for what must
// provide it. Each reader is optional: the seed source uses whichever are registered.

/// <summary>An agent's persisted availability-machine state, with its overlays (doc 09).</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Snapshot">The last snapshot the work-life module saved for the agent's machine.</param>
public sealed record AgentAvailabilityState(Guid AgentId, AvailabilitySnapshot Snapshot);

/// <summary>
/// Reads the saved availability-machine state of every agent. Must be implemented by the work-life store, which is not built yet
/// (no module saves <see cref="AvailabilitySnapshot"/>; a <c>PresenceNotifyingAvailabilityMachine</c> driver would be the writer).
/// </summary>
public interface IAvailabilityStateReader
{
    /// <summary>Lists the saved state of every agent that has one.</summary>
    ValueTask<IReadOnlyList<AgentAvailabilityState>> ListAsync(CancellationToken ct);
}

/// <summary>One agent's schedule inputs for the schedule calculator (doc 09).</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Schedule">Shifts, curfew and zone.</param>
/// <param name="Events">Calendar events that can make the agent unavailable.</param>
/// <param name="Options">Operator additions to the evaluation; null uses the defaults.</param>
public sealed record AgentScheduleInputs(
    Guid AgentId,
    AgentSchedule Schedule,
    IReadOnlyList<CalendarEvent> Events,
    EvaluationOptions? Options = null);

/// <summary>
/// Reads every agent's schedule. Must be implemented over the work-life store (<c>agent_schedule</c> and the calendar), which is
/// not built yet. Used when an agent has no saved state: availability then comes from <see cref="ScheduleEvaluator"/> at "now".
/// </summary>
public interface IAgentScheduleReader
{
    /// <summary>Lists the schedule inputs of every agent that has a schedule.</summary>
    ValueTask<IReadOnlyList<AgentScheduleInputs>> ListAsync(CancellationToken ct);
}

/// <summary>A turn still marked running (doc 06).</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="TurnId">The turn.</param>
/// <param name="StartedAt">When it started (UTC).</param>
/// <param name="GcId">The GC the turn runs in, when known.</param>
/// <param name="GcKind">The kind of that GC.</param>
/// <param name="GcProjectId">The project the GC belongs to, for a project GC.</param>
/// <param name="AgentProjectId">The project the agent works on now.</param>
public sealed record OpenTurn(
    Guid AgentId,
    Guid TurnId,
    DateTimeOffset StartedAt,
    Guid? GcId = null,
    TurnGcKind GcKind = TurnGcKind.Other,
    string? GcProjectId = null,
    string? AgentProjectId = null);

/// <summary>
/// Reads turns whose status is <c>running</c>, for every agent. Must be implemented over <c>turn</c> in <c>Bazaar.Server.Agents</c>:
/// <c>TurnRepository.ListRunningAsync</c> reads one agent at a time, so an adapter needs an all-agents query (or the agent list) and
/// the GC of each turn's session, neither of which that repository offers.
/// </summary>
public interface IOpenTurnReader
{
    /// <summary>Lists the open turns of every agent.</summary>
    ValueTask<IReadOnlyList<OpenTurn>> ListOpenAsync(CancellationToken ct);
}
