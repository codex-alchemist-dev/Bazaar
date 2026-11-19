using Bazaar.Server.Agents;

namespace Bazaar.Server.Agents.Turns.Start;

/// <summary>
/// What the host knows about a turn before it is built: who runs it, under which holder and route, with which tools, and how
/// many due items it may take in one wake. The items themselves come from the inbox.
/// </summary>
public sealed record TurnStartRequest(
    Guid AgentId,
    Guid SessionId,
    TurnRole Role,
    string Holder,
    string Route,
    IReadOnlyList<string> ToolAllowlist,
    int MaxItems);

/// <summary>
/// The result of one start. <see cref="Started"/> is false when nothing was due; then nothing was claimed and no runner was
/// called, and the other fields are empty. Otherwise <see cref="Outcome"/> is the runner's result for the items in
/// <see cref="InboxItemIds"/>, started at <see cref="ChainDepth"/>.
/// </summary>
public sealed record TurnStartResult(
    bool Started,
    TurnRunOutcome? Outcome,
    IReadOnlyList<Guid> InboxItemIds,
    string? WakeReason,
    int ChainDepth)
{
    /// <summary>The result when no inbox item was due.</summary>
    public static TurnStartResult NothingDue { get; } = new(false, null, [], null, 0);
}
