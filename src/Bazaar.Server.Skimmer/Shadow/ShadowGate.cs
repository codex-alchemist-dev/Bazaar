using Bazaar.Server.Skimmer.Digest;
using Bazaar.Server.Skimmer.Rules;
using Bazaar.Server.Skimmer.Scoring;

namespace Bazaar.Server.Skimmer.Shadow;

/// <summary>One member agent for a message: its rules facts and its scoring profile.</summary>
/// <param name="Facts">The rules-stage facts for this agent.</param>
/// <param name="Profile">The agent's scoring profile (subscriptions).</param>
public sealed record ShadowAgent(GcMessageFacts Facts, AgentProfile Profile);

/// <summary>
/// The Skimmer gate in shadow mode (doc 13, "Modes"): it records what it would do and promotes nothing. The type has no
/// inbox writer, so it cannot enqueue a wake or a digest item; the records it returns are the whole effect.
/// </summary>
public sealed class ShadowGate(IRelevanceScorer scorer)
{
    private readonly IRelevanceScorer _scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));

    /// <summary>
    /// Decides one message for each member agent. Mode <see cref="SkimmerMode.Off"/> returns no records, since the gate is
    /// bypassed. Mode <see cref="SkimmerMode.Defer"/> is refused until the shadow gates pass. In shadow mode every record
    /// has <c>Shadow = true</c>; a scored wake means "would promote" only.
    /// </summary>
    /// <exception cref="NotSupportedException">The mode is <see cref="SkimmerMode.Defer"/>.</exception>
    public Task<IReadOnlyList<GateDecision>> DecideAsync(
        GcMessageRef message,
        IReadOnlyList<ShadowAgent> agents,
        ShadowSettings settings,
        CancellationToken ct) =>
        DecideAsync(message, agents, settings, rulesOnly: false, ct);

    /// <summary>
    /// As <see cref="DecideAsync(GcMessageRef, IReadOnlyList{ShadowAgent}, ShadowSettings, CancellationToken)"/>; when
    /// <paramref name="rulesOnly"/> is true the scorer is skipped and unmatched messages defer (queue over its depth limit).
    /// </summary>
    public async Task<IReadOnlyList<GateDecision>> DecideAsync(
        GcMessageRef message,
        IReadOnlyList<ShadowAgent> agents,
        ShadowSettings settings,
        bool rulesOnly,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Mode == SkimmerMode.Defer)
        {
            throw new NotSupportedException("skimmer.mode = defer is not built; it ships after the shadow gates pass (doc 13, C08).");
        }

        if (settings.Mode == SkimmerMode.Off)
        {
            return [];
        }

        var decisions = new GateDecision?[agents.Count];
        var undecided = new List<int>();
        for (var i = 0; i < agents.Count; i++)
        {
            var rule = RulesStage.Evaluate(agents[i].Facts, settings.EffectiveRules);
            if (rule.Decided)
            {
                decisions[i] = Record(agents[i], rule.Path!, rule.Decision!, null, null);
            }
            else
            {
                undecided.Add(i);
            }
        }

        if (undecided.Count > 0 && !rulesOnly)
        {
            var results = await ScoreOrNullAsync(message, undecided.Select(i => agents[i].Profile).ToList(), settings, ct).ConfigureAwait(false);
            foreach (var i in undecided)
            {
                var agent = agents[i];
                var result = results?.FirstOrDefault(r => r.AgentId == agent.Facts.AgentId);
                decisions[i] = result is null
                    ? Record(agent, GatePaths.Default, GateDecisions.Defer, null, null)
                    : result.ScoreBasisPoints >= settings.ThresholdBasisPoints
                        ? Record(agent, GatePaths.Scored, GateDecisions.Wake, result.Category, result.ScoreBasisPoints)
                        : Record(agent, GatePaths.Default, GateDecisions.Defer, result.Category, result.ScoreBasisPoints);
            }
        }

        for (var i = 0; i < decisions.Length; i++)
        {
            decisions[i] ??= Record(agents[i], GatePaths.Default, GateDecisions.Defer, null, null);
        }

        return decisions!;
    }

    /// <summary>A scorer timeout, error or bad result falls back to the rules: the item stays deferred (doc 13, "Failure").</summary>
    private async Task<IReadOnlyList<RelevanceResult>?> ScoreOrNullAsync(
        GcMessageRef message, IReadOnlyList<AgentProfile> profiles, ShadowSettings settings, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.EffectiveScorerTimeout);
        try
        {
            return await _scorer.ScoreAsync(new ScoreRequest(message, profiles), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static GateDecision Record(ShadowAgent agent, string path, string decision, Category? category, int? score) =>
        new(agent.Facts.AgentId, agent.Facts.GcId, agent.Facts.Seq, path, decision, category?.ToWire(), score, Shadow: true);
}
