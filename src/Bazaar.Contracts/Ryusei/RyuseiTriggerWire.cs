namespace Bazaar.Contracts.Ryusei;

/// <summary>Payload of the <c>ryusei.trigger.pause</c> and <c>ryusei.trigger.resume</c> requests: the trigger to change.</summary>
/// <param name="TriggerId">Trigger id.</param>
public sealed record RyuseiTriggerChangeRequest(Guid TriggerId);

/// <summary>Reply to a pause or resume: the trigger and its version after the change.</summary>
/// <param name="TriggerId">Trigger id.</param>
/// <param name="Version">Row version after the change. A client keeps it for a later change.</param>
public sealed record RyuseiTriggerChangeReply(Guid TriggerId, long Version);
