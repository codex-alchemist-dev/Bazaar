using Bazaar.Server.Skimmer.Rules;
using Bazaar.Server.Skimmer.Scoring;

namespace Bazaar.Server.Skimmer.Shadow;

/// <summary>One GC message with the rules facts for the consumer's agent.</summary>
public sealed record ShadowMessage(GcMessageRef Message, GcMessageFacts Facts);

/// <summary>Reads GC messages in seq order. The GC log is the source of truth (doc 13, "Restart").</summary>
public interface IShadowMessageSource
{
    /// <summary>Up to <paramref name="limit"/> messages of the GC after <paramref name="afterSeq"/>, oldest first, with facts for this agent.</summary>
    Task<IReadOnlyList<ShadowMessage>> ReadAfterAsync(Guid gcId, ShadowAgentInfo agent, long afterSeq, int limit, CancellationToken ct);
}

/// <summary>The agent a consumer runs for.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Profile">Its scoring profile.</param>
public sealed record ShadowAgentInfo(Guid AgentId, AgentProfile Profile);

/// <summary>
/// One consumer per agent (doc 13, "Concurrency"): reads messages in seq order, decides each through the
/// <see cref="ShadowGate"/> and records it. Restart-safe: the cursor is the highest recorded seq, so missed seqs are
/// re-decided and a duplicate decision is a no-op. Above <c>skimmer.queue_depth_limit</c> pending messages it uses the rules
/// only and skips the scorer. It never wakes or enqueues anything.
/// </summary>
public sealed class ShadowConsumer(ShadowGate gate, IShadowMessageSource source, IShadowDecisionStore store, string? scorerModelId = null)
{
    /// <summary>Decides and records up to <paramref name="batchSize"/> pending messages of one GC. Returns how many were newly recorded.</summary>
    public async Task<int> RunOnceAsync(
        Guid gcId, ShadowAgentInfo agent, ShadowSettings settings, int queueDepthLimit, int batchSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (settings.Mode != SkimmerMode.Shadow)
        {
            return 0;
        }

        var cursor = await store.LastDecidedSeqAsync(gcId, agent.AgentId, ct).ConfigureAwait(false);
        var pending = await source.ReadAfterAsync(gcId, agent, cursor, Math.Max(batchSize, queueDepthLimit + 1), ct).ConfigureAwait(false);
        var rulesOnly = pending.Count > queueDepthLimit;

        var recorded = 0;
        foreach (var item in pending.OrderBy(m => m.Message.Seq).Take(batchSize))
        {
            var decisions = await gate.DecideAsync(
                item.Message, [new ShadowAgent(item.Facts, agent.Profile)], settings, rulesOnly, ct).ConfigureAwait(false);
            foreach (var decision in decisions)
            {
                if (await store.RecordAsync(decision, scorerModelId, ct).ConfigureAwait(false))
                {
                    recorded++;
                }
            }
        }

        return recorded;
    }
}
