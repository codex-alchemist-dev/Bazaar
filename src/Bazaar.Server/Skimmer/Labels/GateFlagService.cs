using Bazaar.Server.Skimmer.Data;

namespace Bazaar.Server.Skimmer.Labels;

/// <summary>The result of one flag. <c>Reason</c> is a short description of the outcome, not the agent's flag reason, which is not stored.</summary>
public sealed record FlagResult(bool Recorded, string Reason);

/// <summary>
/// Agent flags on wake decisions (doc 13, "Feedback"): the logic behind the agent tools <c>flag_missed(seq)</c> and
/// <c>flag_false_wake(seq)</c>. Each flag writes one <c>gate_labels</c> row with source <c>agent_flag</c> on the decision
/// that the agent itself received for that message, the most recent one if several prompt versions exist.
///
/// Flags never change thresholds or config. This service does not read or write <c>gate_config</c> or the settings
/// table, and nothing here feeds a label back into the gate. Reasons are not stored: the schema has no column for them.
///
/// TODO(component): wire these two methods as the agent tools flag_missed and flag_false_wake once the agent tool
/// surface exists. The tool registration is out of scope for this work package.
/// </summary>
public sealed class GateFlagService(GateLabelRepository labels, WakeDecisionRepository decisions)
{
    /// <summary>Flags a decision the agent should have been woken for and was not (label <c>missed</c>).</summary>
    public Task<FlagResult> FlagMissedAsync(Guid agentId, Guid gcId, long seq, Guid labeller, CancellationToken ct) =>
        RecordAsync(GateLabelValues.Missed, agentId, gcId, seq, labeller, ct);

    /// <summary>Flags a decision that woke the agent when it should have deferred (label <c>false_wake</c>).</summary>
    public Task<FlagResult> FlagFalseWakeAsync(Guid agentId, Guid gcId, long seq, Guid labeller, CancellationToken ct) =>
        RecordAsync(GateLabelValues.FalseWake, agentId, gcId, seq, labeller, ct);

    private async Task<FlagResult> RecordAsync(string label, Guid agentId, Guid gcId, long seq, Guid labeller, CancellationToken ct)
    {
        if (agentId == Guid.Empty) throw new ArgumentException("The agent id must not be empty.", nameof(agentId));
        if (gcId == Guid.Empty) throw new ArgumentException("The GC id must not be empty.", nameof(gcId));
        if (seq < 1) throw new ArgumentOutOfRangeException(nameof(seq), seq, "The sequence number must be at least 1.");
        if (labeller == Guid.Empty) throw new ArgumentException("The labeller id must not be empty.", nameof(labeller));

        var decision = await decisions.FindLatestAsync(gcId, seq, agentId, ct).ConfigureAwait(false);
        if (decision is null)
            return new FlagResult(false, "not found");

        await labels.InsertAsync(
            new NewGateLabel(Guid.CreateVersion7(), decision.Id, label, GateLabelValues.SourceAgentFlag, labeller),
            ct: ct).ConfigureAwait(false);
        return new FlagResult(true, "recorded");
    }
}
