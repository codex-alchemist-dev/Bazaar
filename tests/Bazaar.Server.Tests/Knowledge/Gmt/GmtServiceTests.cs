using Bazaar.Server.Knowledge;
using Bazaar.Server.Knowledge.Gmt;
using Bazaar.Server.Memory.Trust;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bazaar.Server.Tests.Knowledge.Gmt;

public sealed class GmtServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.Zero));
    private readonly InMemoryGmtStore _store = new();
    private readonly GmtService _service;

    public GmtServiceTests()
    {
        _service = new GmtService(_store, LessonReviewer.Architect, _clock);
    }

    [Fact]
    public async Task Candidate_from_clean_child_is_assigned_T2_and_staged()
    {
        var candidate = await _service.StageCandidateAsync(
            childAgentId: "subagent-1",
            parentAgentId: "parent-alex",
            traceRef: "turn:123",
            topic: "git-concurrency",
            lessonText: "Always lock the repository before invoking external merge scripts.",
            childTainted: false,
            ct: Ct);

        Assert.NotNull(candidate);
        Assert.Equal(MemoryTier.AgentVerified, candidate.TrustTier); // T2 for clean agent write
        Assert.False(candidate.ChildTainted);
        Assert.Equal(GmtCandidateStatus.Staged, candidate.Status);
        Assert.Equal(_clock.GetUtcNow(), candidate.CreatedAt);

        var staged = await _service.ListStagedAsync("parent-alex", ct: Ct);
        Assert.Single(staged);
        Assert.Equal(candidate.CandidateId, staged[0].CandidateId);
    }

    [Fact]
    public async Task Candidate_from_tainted_child_is_capped_at_T1_untrusted_derived()
    {
        // Enforce Floor (doc 25 line 108): A candidate from a tainted child stays T1
        var candidate = await _service.StageCandidateAsync(
            childAgentId: "subagent-tainted",
            parentAgentId: "parent-alex",
            traceRef: "turn:124",
            topic: "api-handling",
            lessonText: "Untrusted endpoint requires secondary sanitization.",
            childTainted: true,
            ct: Ct);

        Assert.NotNull(candidate);
        Assert.Equal(MemoryTier.UntrustedDerived, candidate.TrustTier); // Capped at T1
        Assert.True(candidate.ChildTainted);
        Assert.Equal(GmtCandidateStatus.Staged, candidate.Status);
    }

    [Fact]
    public async Task Staged_candidate_is_strictly_invisible_to_parent_active_retrieval()
    {
        // Invariant (doc 25 §2, §3): staged candidates are not retrievable until accepted
        await _service.StageCandidateAsync(
            childAgentId: "subagent-1",
            parentAgentId: "parent-alex",
            traceRef: "turn:125",
            topic: "caching",
            lessonText: "Use Garnet for distributed caching.",
            childTainted: false,
            ct: Ct);

        var activeLessons = await _service.ListActiveLessonsAsync("parent-alex", ct: Ct);

        Assert.Empty(activeLessons); // Invisible until accepted
    }

    [Fact]
    public async Task Review_accepting_candidate_makes_it_visible_in_active_retrieval()
    {
        var candidate = await _service.StageCandidateAsync(
            childAgentId: "subagent-1",
            parentAgentId: "parent-alex",
            traceRef: "turn:126",
            topic: "postgres",
            lessonText: "Use NpgsqlBatch for high-throughput writes.",
            childTainted: false,
            ct: Ct);

        _clock.Advance(TimeSpan.FromHours(1));

        var reviewResult = await _service.ReviewCandidateAsync(
            candidate.CandidateId,
            GmtCandidateStatus.Accepted,
            reviewer: "architect-lead",
            ct: Ct);

        Assert.True(reviewResult.Succeeded);
        Assert.NotNull(reviewResult.Candidate);
        Assert.Equal(GmtCandidateStatus.Accepted, reviewResult.Candidate.Status);
        Assert.Equal("architect-lead", reviewResult.Candidate.ReviewedBy);
        Assert.Equal(_clock.GetUtcNow(), reviewResult.Candidate.ReviewedAt);

        var activeLessons = await _service.ListActiveLessonsAsync("parent-alex", ct: Ct);
        Assert.Single(activeLessons);
        Assert.Equal("postgres", activeLessons[0].Topic);
    }

    [Fact]
    public async Task Review_with_edits_records_edited_text_and_makes_it_active()
    {
        var candidate = await _service.StageCandidateAsync(
            childAgentId: "subagent-1",
            parentAgentId: "parent-alex",
            traceRef: "turn:127",
            topic: "testing",
            lessonText: "Run unit tests without fixtures.",
            childTainted: false,
            ct: Ct);

        var editedText = "Run unit tests with FakeTimeProvider and in-memory stores.";

        var reviewResult = await _service.ReviewCandidateAsync(
            candidate.CandidateId,
            GmtCandidateStatus.Edited,
            reviewer: "architect-lead",
            editedText: editedText,
            ct: Ct);

        Assert.True(reviewResult.Succeeded);
        Assert.Equal(GmtCandidateStatus.Edited, reviewResult.Candidate!.Status);
        Assert.Equal(editedText, reviewResult.Candidate.EditedLessonText);

        var activeLessons = await _service.ListActiveLessonsAsync("parent-alex", ct: Ct);
        Assert.Single(activeLessons);
        Assert.Equal(editedText, activeLessons[0].EditedLessonText);
    }

    [Fact]
    public async Task Review_rejecting_candidate_keeps_it_invisible_to_active_retrieval()
    {
        var candidate = await _service.StageCandidateAsync(
            childAgentId: "subagent-1",
            parentAgentId: "parent-alex",
            traceRef: "turn:128",
            topic: "bad-pattern",
            lessonText: "Ignore timeout exceptions.",
            childTainted: false,
            ct: Ct);

        var reviewResult = await _service.ReviewCandidateAsync(
            candidate.CandidateId,
            GmtCandidateStatus.Rejected,
            reviewer: "architect-lead",
            ct: Ct);

        Assert.True(reviewResult.Succeeded);

        var activeLessons = await _service.ListActiveLessonsAsync("parent-alex", ct: Ct);
        Assert.Empty(activeLessons);

        var staged = await _service.ListStagedAsync("parent-alex", ct: Ct);
        Assert.Empty(staged); // No longer in staged queue
    }

    [Fact]
    public async Task Owner_escalation_floor_forces_owner_reviewer_when_tool_grant_or_charter_present()
    {
        // Enforce Floor (doc 25 line 86): Tool grants and charter text always go to the owner;
        // gmt.lesson_reviewer cannot route them elsewhere.
        var candidateWithTool = await _service.StageCandidateAsync(
            childAgentId: "subagent-1",
            parentAgentId: "parent-alex",
            traceRef: "turn:129",
            topic: "permissions",
            lessonText: "Need to grant tool bash.exec for deep file inspections.",
            childTainted: false,
            ct: Ct);

        var requiredReviewer = _service.DetermineRequiredReviewer(candidateWithTool);
        Assert.Equal(LessonReviewer.Owner, requiredReviewer);

        var normalCandidate = await _service.StageCandidateAsync(
            childAgentId: "subagent-1",
            parentAgentId: "parent-alex",
            traceRef: "turn:130",
            topic: "code-style",
            lessonText: "Use expression-bodied members for short getters.",
            childTainted: false,
            ct: Ct);

        var normalReviewer = _service.DetermineRequiredReviewer(normalCandidate);
        Assert.Equal(LessonReviewer.Architect, normalReviewer); // Follows default setting
    }
}
