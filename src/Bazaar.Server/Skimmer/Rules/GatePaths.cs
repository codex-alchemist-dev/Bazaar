namespace Bazaar.Server.Skimmer.Rules;

/// <summary>
/// The path values stored in <c>wake_decisions.path</c>. They must match the CHECK constraint in migration 0016
/// (<c>src/Bazaar.Server/Data/Sql/common/0016_skimmer_gate.sql</c>).
/// </summary>
public static class GatePaths
{
    /// <summary>An @mention of the agent or one of its handles.</summary>
    public const string Mention = "mention";

    /// <summary>A two-member GC with the agent.</summary>
    public const string Dm = "dm";

    /// <summary>A blocked message, a review request, a called decision, or an owner message.</summary>
    public const string Critical = "critical";

    /// <summary>A sender in quarantine (doc 22). Always defers.</summary>
    public const string Quarantine = "quarantine";

    /// <summary>Anything the scored stage decides. The rules stage never emits it.</summary>
    public const string Scored = "scored";

    /// <summary>Anything else with no subscription match. Emitted only by the scorer stage when it has nothing to score.</summary>
    public const string Default = "default";
}

/// <summary>The values stored in <c>wake_decisions.decision</c>. They must match the CHECK constraint in migration 0016.</summary>
public static class GateDecisions
{
    /// <summary>The agent's turn runs now.</summary>
    public const string Wake = "wake";

    /// <summary>The message waits in the agent's digest.</summary>
    public const string Defer = "defer";
}
