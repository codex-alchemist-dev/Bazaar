namespace Bazaar.Server.Skimmer.Data;

/// <summary>A <c>gate_config</c> row (doc 13). Mode and thresholds are settings, so they are not here.</summary>
/// <param name="AgentId">The agent this gate config belongs to.</param>
/// <param name="SubscriptionsJson">The scored-path subscriptions as JSON text. An empty array scores nothing.</param>
public sealed record GateConfig(Guid AgentId, string SubscriptionsJson);

/// <summary>A decision to insert into <c>wake_decisions</c>. The caller supplies the id, as the Kanban repositories do.</summary>
public sealed record NewWakeDecision(
    Guid Id,
    Guid GcId,
    long Seq,
    Guid AgentId,
    string Path,
    string Decision,
    bool Shadow,
    int? ScoreBp,
    string? Category,
    string? ModelId,
    string PromptVersion);

/// <summary>A stored <c>wake_decisions</c> row.</summary>
public sealed record WakeDecisionRecord(
    Guid Id,
    Guid GcId,
    long Seq,
    Guid AgentId,
    string Path,
    string Decision,
    bool Shadow,
    int? ScoreBp,
    string? Category,
    string? ModelId,
    string PromptVersion,
    DateTimeOffset CreatedAt);

/// <summary>A label to insert into <c>gate_labels</c>.</summary>
public sealed record NewGateLabel(Guid Id, Guid DecisionId, string Label, string Source, Guid Labeller);

/// <summary>A stored <c>gate_labels</c> row.</summary>
public sealed record GateLabelRecord(Guid Id, Guid DecisionId, string Label, string Source, Guid Labeller, DateTimeOffset CreatedAt);
