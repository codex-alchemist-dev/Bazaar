using Bazaar.Server.Fun.Mirror;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bazaar.Server.Tests.Fun.Mirror;

public sealed class MirrorEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero));
    private readonly InMemoryMirrorStore _store = new();

    [Fact]
    public async Task Rules_below_evidence_threshold_are_excluded_from_synthesized_profile()
    {
        var settings = new MirrorSettings { EvidenceThreshold = 5 };
        var engine = new MirrorEngine(settings, _store, _clock);

        // Ingest 4 observations for the topic (threshold is 5)
        for (var i = 1; i <= 4; i++)
        {
            await engine.IngestSignalAsync(
                kind: "accept/reject",
                sourceRef: $"card:{i}",
                payload: "prefer clean commits",
                ruleTopic: "commits",
                ruleInstruction: "keep commits squashed and clean",
                ct: Ct);
        }

        var profile = await engine.SynthesizeProfileAsync(ct: Ct);

        Assert.NotNull(profile);
        Assert.Equal(1, profile.Version);
        Assert.Empty(profile.Rules); // Excluded because 4 < 5
    }

    [Fact]
    public async Task Rules_meeting_or_exceeding_evidence_threshold_are_included()
    {
        var settings = new MirrorSettings { EvidenceThreshold = 5 };
        var engine = new MirrorEngine(settings, _store, _clock);

        // Ingest 5 observations
        for (var i = 1; i <= 5; i++)
        {
            await engine.IngestSignalAsync(
                kind: "accept/reject",
                sourceRef: $"card:{i}",
                payload: "prefer clean commits",
                ruleTopic: "commits",
                ruleInstruction: "keep commits squashed and clean",
                ct: Ct);
        }

        var profile = await engine.SynthesizeProfileAsync(ct: Ct);

        Assert.NotNull(profile);
        Assert.Single(profile.Rules);
        var rule = profile.Rules[0];
        Assert.Equal("commits", rule.Topic);
        Assert.Equal("keep commits squashed and clean", rule.Instruction);
        Assert.Equal(5, rule.EvidenceCount);
        Assert.Equal(5, rule.EvidenceRefs.Count);
    }

    [Fact]
    public async Task Annotating_suggestion_preserves_original_text_as_invariant()
    {
        var settings = new MirrorSettings { EvidenceThreshold = 2 };
        var engine = new MirrorEngine(settings, _store, _clock);

        await engine.IngestSignalAsync("owner edits", "gc:1", "edit", "performance", "use spans instead of substring", ct: Ct);
        await engine.IngestSignalAsync("owner edits", "gc:2", "edit", "performance", "use spans instead of substring", ct: Ct);

        var profile = await engine.SynthesizeProfileAsync(ct: Ct);

        var originalText = "Let's optimize performance by inspecting data buffers.";
        var annotation = engine.AnnotateSuggestion("sug-1", originalText, profile);

        // Critical invariant: Original text is never removed or replaced (doc 26 §5)
        Assert.Equal(originalText, annotation.OriginalText);
        Assert.Equal("sug-1", annotation.SuggestionId);
        Assert.Single(annotation.AppliedRuleIds);
        Assert.NotNull(annotation.OfferedRewrite);
        Assert.True(annotation.RankingScore > 0);
    }

    [Fact]
    public async Task Rewrite_offer_omitted_when_allow_rewrites_is_false()
    {
        var settings = new MirrorSettings
        {
            EvidenceThreshold = 2,
            AllowRewriteOffers = false, // Disabled
        };
        var engine = new MirrorEngine(settings, _store, _clock);

        await engine.IngestSignalAsync("owner edits", "gc:1", "edit", "performance", "use spans", ct: Ct);
        await engine.IngestSignalAsync("owner edits", "gc:2", "edit", "performance", "use spans", ct: Ct);

        var profile = await engine.SynthesizeProfileAsync(ct: Ct);

        var annotation = engine.AnnotateSuggestion("sug-1", "Focus on performance improvements", profile);

        Assert.Equal("Focus on performance improvements", annotation.OriginalText);
        Assert.Single(annotation.AppliedRuleIds);
        Assert.Null(annotation.OfferedRewrite); // Omitted because AllowRewriteOffers is false
    }

    [Fact]
    public async Task Paused_learning_skips_signal_ingestion()
    {
        var settings = new MirrorSettings { EvidenceThreshold = 1, LearningPaused = true };
        var engine = new MirrorEngine(settings, _store, _clock);

        var ingested = await engine.IngestSignalAsync(
            "review comments", "review:1", "notes", "architecture", "keep contracts minimal", ct: Ct);

        Assert.False(ingested);

        var signals = await _store.ListSignalsAsync(ct: Ct);
        Assert.Empty(signals);

        var profile = await engine.SynthesizeProfileAsync(ct: Ct);
        Assert.Empty(profile.Rules);
    }

    [Fact]
    public async Task Wipe_clears_signals_profiles_and_learned_rules()
    {
        var settings = new MirrorSettings { EvidenceThreshold = 1 };
        var engine = new MirrorEngine(settings, _store, _clock);

        await engine.IngestSignalAsync("explicit statements", "settings:1", "no tabs", "style", "use spaces", ct: Ct);
        var profile = await engine.SynthesizeProfileAsync(ct: Ct);
        Assert.Single(profile.Rules);

        await engine.WipeAsync(ct: Ct);

        var signals = await _store.ListSignalsAsync(ct: Ct);
        Assert.Empty(signals);

        var currentProfile = await _store.GetProfileAsync(ct: Ct);
        Assert.Null(currentProfile);

        var newProfile = await engine.SynthesizeProfileAsync(ct: Ct);
        Assert.Empty(newProfile.Rules);
    }

    [Fact]
    public async Task Delete_rule_removes_specific_rule()
    {
        var settings = new MirrorSettings { EvidenceThreshold = 1 };
        var engine = new MirrorEngine(settings, _store, _clock);

        await engine.IngestSignalAsync("accept/reject", "gc:1", "ok", "topic1", "instruction1", ct: Ct);
        var profile = await engine.SynthesizeProfileAsync(ct: Ct);
        Assert.Single(profile.Rules);

        var ruleId = profile.Rules[0].RuleId;
        var deleted = await engine.DeleteRuleAsync(ruleId, ct: Ct);
        Assert.True(deleted);

        var updatedProfile = await engine.SynthesizeProfileAsync(ct: Ct);
        Assert.Empty(updatedProfile.Rules);
    }
}
