using System.Collections.Concurrent;

namespace Bazaar.Server.Fun.Mirror;

/// <summary>
/// Engine for the Mirror Agent preference model (docs/design/26-fun-systems.md §5).
/// Pure logic and storage orchestration:
/// - Learns only from owner decisions (accept/reject, edits, review comments, Council tie-breaks, explicit statements).
/// - Enforces <c>mirror.evidence_threshold</c>: rules below threshold never apply.
/// - Annotates suggestions in the owner view while keeping the original strictly visible (fixed invariant).
/// - Supports owner controls: wipe, pause learning, rule deletion.
/// </summary>
public sealed class MirrorEngine
{
    private readonly MirrorSettings _settings;
    private readonly IMirrorStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, (string Topic, string Instruction, ConcurrentBag<string> EvidenceRefs)> _candidates = new(StringComparer.OrdinalIgnoreCase);

    public MirrorEngine(
        MirrorSettings settings,
        IMirrorStore store,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        settings.Validate();
        _settings = settings;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MirrorSettings Settings => _settings;

    /// <summary>
    /// Ingests an owner decision observation into the signal log and candidate rule ledger.
    /// If <see cref="MirrorSettings.LearningPaused"/> is true, signal ingestion is skipped.
    /// </summary>
    public async Task<bool> IngestSignalAsync(
        string kind,
        string sourceRef,
        string payload,
        string? ruleTopic = null,
        string? ruleInstruction = null,
        double weight = 1.0,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);

        if (_settings.LearningPaused)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var signal = new MirrorSignal(
            Id: Guid.NewGuid(),
            Kind: kind,
            SourceRef: sourceRef,
            Weight: weight,
            Payload: payload,
            CreatedAt: now);

        await _store.RecordSignalAsync(signal, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(ruleTopic) && !string.IsNullOrWhiteSpace(ruleInstruction))
        {
            var ruleKey = $"{ruleTopic}:{ruleInstruction}".ToLowerInvariant();
            _candidates.AddOrUpdate(
                ruleKey,
                _ => (ruleTopic, ruleInstruction, [sourceRef]),
                (_, existing) =>
                {
                    existing.EvidenceRefs.Add(sourceRef);
                    return existing;
                });
        }

        return true;
    }

    /// <summary>
    /// Synthesizes an updated versioned <see cref="MirrorProfile"/> containing rules that meet
    /// or exceed <c>mirror.evidence_threshold</c>. Rules below the threshold are excluded.
    /// </summary>
    public async Task<MirrorProfile> SynthesizeProfileAsync(CancellationToken ct = default)
    {
        var activeRules = new List<MirrorRule>();
        var now = _timeProvider.GetUtcNow();

        foreach (var (key, value) in _candidates)
        {
            var refs = value.EvidenceRefs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var count = refs.Count;

            // Enforce floor: rule applies only above mirror.evidence_threshold (doc 26 §5)
            if (count >= _settings.EvidenceThreshold)
            {
                activeRules.Add(new MirrorRule(
                    RuleId: key,
                    Topic: value.Topic,
                    Instruction: value.Instruction,
                    EvidenceCount: count,
                    EvidenceRefs: refs,
                    CreatedAt: now));
            }
        }

        var version = await _store.GetNextProfileVersionAsync(ct).ConfigureAwait(false);
        var profile = new MirrorProfile(
            Version: version,
            Rules: activeRules.OrderByDescending(r => r.EvidenceCount).ToList(),
            CreatedAt: now);

        await _store.SaveProfileAsync(profile, ct).ConfigureAwait(false);
        return profile;
    }

    /// <summary>
    /// Annotates an agent suggestion for the owner view.
    /// Crucial invariant: <paramref name="originalText"/> is always kept visible; Mirror never removes an item.
    /// </summary>
    public MirrorAnnotation AnnotateSuggestion(
        string suggestionId,
        string originalText,
        MirrorProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalText);
        ArgumentNullException.ThrowIfNull(profile);

        var appliedRuleIds = new List<string>();
        string? rewriteOffer = null;
        var rankingScore = 0.0;

        foreach (var rule in profile.Rules)
        {
            // Simple keyword/topic match for demonstration & testability
            if (originalText.Contains(rule.Topic, StringComparison.OrdinalIgnoreCase))
            {
                appliedRuleIds.Add(rule.RuleId);
                rankingScore += rule.EvidenceCount * 1.5;

                if (_settings.AllowRewriteOffers && rewriteOffer == null)
                {
                    rewriteOffer = $"[Mirror Suggestion: {rule.Instruction}] {originalText}";
                }
            }
        }

        return new MirrorAnnotation(
            SuggestionId: suggestionId,
            OriginalText: originalText, // Original always stays visible (fixed invariant)
            AppliedRuleIds: appliedRuleIds,
            OfferedRewrite: rewriteOffer,
            RankingScore: rankingScore);
    }

    /// <summary>Owner control: wipes profile, learned rules, and signals.</summary>
    public async Task WipeAsync(CancellationToken ct = default)
    {
        _candidates.Clear();
        await _store.WipeProfileAndEmbeddingsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Owner control: deletes an individual rule.</summary>
    public async Task<bool> DeleteRuleAsync(string ruleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        _candidates.TryRemove(ruleId, out _);
        return await _store.DeleteRuleAsync(ruleId, ct).ConfigureAwait(false);
    }
}
