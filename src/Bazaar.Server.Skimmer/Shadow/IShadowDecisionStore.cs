using Bazaar.Server.Skimmer.Data;
using Bazaar.Server.Skimmer.Digest;
using Bazaar.Server.Skimmer.Scoring;

namespace Bazaar.Server.Skimmer.Shadow;

/// <summary>Where shadow decisions are recorded and where the consumer's cursor comes from.</summary>
public interface IShadowDecisionStore
{
    /// <summary>The highest seq already decided for this (gc, agent) under the current prompt version, or 0.</summary>
    Task<long> LastDecidedSeqAsync(Guid gcId, Guid agentId, CancellationToken ct);

    /// <summary>Records one decision. A repeat of the same (gc, seq, agent, prompt version) is a no-op and returns false.</summary>
    Task<bool> RecordAsync(GateDecision decision, string? modelId, CancellationToken ct);
}

/// <summary><see cref="IShadowDecisionStore"/> over <c>wake_decisions</c>. It writes only that table: no inbox item, no wake.</summary>
public sealed class PostgresShadowDecisionStore(WakeDecisionRepository repository) : IShadowDecisionStore
{
    /// <inheritdoc />
    public Task<long> LastDecidedSeqAsync(Guid gcId, Guid agentId, CancellationToken ct) =>
        repository.MaxSeqAsync(gcId, agentId, ScorePromptBuilder.PromptVersion, ct);

    /// <inheritdoc />
    public Task<bool> RecordAsync(GateDecision decision, string? modelId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return repository.InsertAsync(
            new NewWakeDecision(
                Guid.CreateVersion7(), decision.GcId, decision.Seq, decision.AgentId, decision.Path, decision.Decision,
                decision.Shadow, decision.ScoreBasisPoints, decision.Category, decision.ScoreBasisPoints is null ? null : modelId,
                ScorePromptBuilder.PromptVersion),
            ct: ct);
    }
}
