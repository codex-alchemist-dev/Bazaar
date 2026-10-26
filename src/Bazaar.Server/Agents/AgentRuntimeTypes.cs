namespace Bazaar.Server.Agents;

/// <summary>Agent lifecycle state (doc 06, "Lifecycle"). Stored as snake_case text.</summary>
public enum AgentState
{
    Created,
    Onboarding,
    Active,
    Resting,
    Retired,
}

/// <summary>Who a turn acts for. A reader turn holds external content and has no write tools (doc 06, doc 15).</summary>
public enum TurnRole
{
    Actor,
    Reader,
}

/// <summary>Turn status. A turn is <c>Running</c> until it ends once; <c>Abandoned</c> is set by crash recovery.</summary>
public enum TurnStatus
{
    Running,
    Completed,
    Failed,
    Abandoned,
}

/// <summary>Kind of one turn step (doc 06, <c>turn_step.kind</c>).</summary>
public enum TurnStepKind
{
    Model,
    Tool,
    Post,
    Memory,
}

/// <summary>Step status. A pending step is completed in place, or marked abandoned or unknown on recovery.</summary>
public enum TurnStepStatus
{
    Pending,
    Completed,
    Abandoned,
    Unknown,
}

/// <summary>Inbox item state. A claimed item belongs to exactly one holder until it is completed or released.</summary>
public enum InboxState
{
    Pending,
    Claimed,
    Done,
}
