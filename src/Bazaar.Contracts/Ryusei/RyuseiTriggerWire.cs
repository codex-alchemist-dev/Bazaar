namespace Bazaar.Contracts.Ryusei;

/// <summary>Payload of the <c>ryusei.trigger.pause</c> and <c>ryusei.trigger.resume</c> requests: the trigger to change.</summary>
/// <param name="TriggerId">Trigger id.</param>
public sealed record RyuseiTriggerChangeRequest(Guid TriggerId);

/// <summary>Reply to a pause or resume: the trigger and its version after the change.</summary>
/// <param name="TriggerId">Trigger id.</param>
/// <param name="Version">Row version after the change. A client keeps it for a later change.</param>
public sealed record RyuseiTriggerChangeReply(Guid TriggerId, long Version);

/// <summary>Payload of the <c>ryusei.trigger.list</c> request: the project whose triggers the owner wants.</summary>
/// <param name="ProjectId">Project id.</param>
public sealed record RyuseiTriggerListRequest(Guid ProjectId);

/// <summary>One trigger as a board chip. <see cref="TaskId"/> is the card it sits on, or null for a trigger with no task.</summary>
/// <param name="TriggerId">Trigger id.</param>
/// <param name="TaskId">The task the trigger is attached to, or null.</param>
/// <param name="Kind">Trigger kind: timer, interval, watcher or event.</param>
/// <param name="State">Trigger state, as the wire spells it.</param>
/// <param name="NextFireAt">When it next fires, or null.</param>
/// <param name="Purpose">What the trigger is for. Only the owner reads this list.</param>
/// <param name="Version">Row version, for a later pause or resume.</param>
public sealed record RyuseiTriggerChip(
    Guid TriggerId,
    Guid? TaskId,
    string Kind,
    string State,
    DateTimeOffset? NextFireAt,
    string Purpose,
    long Version);

/// <summary>Reply to <c>ryusei.trigger.list</c>: the project's triggers, oldest first.</summary>
/// <param name="ProjectId">Project id.</param>
/// <param name="Triggers">Triggers in creation order.</param>
public sealed record RyuseiTriggerListReply(Guid ProjectId, IReadOnlyList<RyuseiTriggerChip> Triggers);
