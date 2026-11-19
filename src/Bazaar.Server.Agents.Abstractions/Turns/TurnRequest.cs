using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// What one turn needs to start. <see cref="Messages"/> is the context already assembled by the caller (doc 06, "Context
/// assembly"); its hash keys step reuse on recovery, so the same context must give the same messages.
/// <see cref="InboxClaimLimit"/> is how many due inbox items the turn claims; zero claims none.
/// <see cref="InboxItemIds"/>, when non-empty, claims exactly those items (the ones still pending and due), and then
/// <see cref="InboxClaimLimit"/> must be zero.
/// <see cref="Tools"/> are offered to the model on an actor turn; a reader turn offers none.
/// <see cref="MessageBuilder"/>, when set, builds the messages after the turn row exists and receives its id, so the context
/// manifest can record it (doc 06). It then takes the place of <see cref="Messages"/>, which may be empty.
/// <see cref="ChainDepth"/> is the wake chain depth the tool dispatcher receives with each call (doc 15); zero for no chain.
/// </summary>
public sealed record TurnRequest(
    Guid AgentId,
    Guid SessionId,
    string WakeReason,
    string Route,
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ModelToolDefinition>? Tools = null,
    int InboxClaimLimit = 0,
    Func<Guid, CancellationToken, Task<IReadOnlyList<ModelMessage>>>? MessageBuilder = null,
    TurnRole Role = TurnRole.Actor,
    int? StepCapOverride = null,
    IReadOnlyList<Guid>? InboxItemIds = null,
    int ChainDepth = 0);

/// <summary>How a turn run ended.</summary>
public enum TurnOutcome
{
    /// <summary>Another holder had the agent's lease. Nothing was written and no model was called.</summary>
    NotAcquired,

    /// <summary>The model gave a final message; claimed items are done and the turn is completed.</summary>
    Completed,

    /// <summary>The turn ended without a final message; claimed items were returned to pending.</summary>
    Failed,
}

/// <summary>The result of one turn run. Fields that do not apply to the outcome are null or zero.</summary>
/// <param name="Outcome">How the run ended.</param>
/// <param name="TurnId">The turn row, or null when the lease was not acquired.</param>
/// <param name="FinalText">The final message when the turn completed.</param>
/// <param name="FailureReason">A short reason when the turn failed.</param>
/// <param name="ModelSteps">Model steps written in this run, including reused ones.</param>
/// <param name="ReusedSteps">Model steps whose response was reused from an abandoned turn instead of a gateway call.</param>
/// <param name="ClaimedItems">Inbox items claimed by this run.</param>
/// <param name="EndReason">The turn's end reason in <see cref="TurnEndReason"/> terms, when the run recorded one. Null for failures with no matching reason.</param>
public sealed record TurnResult(
    TurnOutcome Outcome,
    Guid? TurnId,
    string? FinalText,
    string? FailureReason,
    int ModelSteps,
    int ReusedSteps,
    int ClaimedItems,
    TurnEndReason? EndReason = null)
{
    /// <summary>The result for a run that did not get the lease.</summary>
    public static TurnResult NotAcquired { get; } = new(TurnOutcome.NotAcquired, null, null, null, 0, 0, 0);
}
