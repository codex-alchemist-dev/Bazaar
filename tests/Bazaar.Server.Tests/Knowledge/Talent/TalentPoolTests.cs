using Bazaar.Server.Council.Membership;
using Bazaar.Server.Knowledge.Talent;
using Bazaar.Server.Sanitization;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bazaar.Server.Tests.Knowledge.Talent;

public sealed class TalentPoolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly ITextSanitizer _sanitizer = new TextSanitizer();
    private readonly InMemoryTalentPoolStore _store = new();
    private readonly TalentManifestValidator _validator;
    private readonly TalentPoolService _service;
    private readonly CouncilHireHook _hireHook;

    public TalentPoolTests()
    {
        _validator = new TalentManifestValidator(_sanitizer);
        _service = new TalentPoolService(_store, _validator, _clock);
        _hireHook = new CouncilHireHook(_service);
    }

    private static TalentManifest CreateValidManifest(
        string templateId = "code-reviewer",
        string prompt = "You are a code reviewer focusing on correctness and performance.") =>
        new(
            TemplateId: templateId,
            Name: "Senior Code Reviewer",
            Role: "Code Review Specialist",
            Prompt: prompt,
            ToolAllowlist: ["git.diff", "compiler.check"],
            ModelWhitelist: ["anthropic:claude-3-5-sonnet", "openai:gpt-4o"],
            Specialties: ["dotnet", "csharp", "security"]);

    [Fact]
    public async Task Manifest_with_valid_data_submits_successfully_and_is_quarantined()
    {
        var manifest = CreateValidManifest();
        var result = await _service.SubmitAsync(manifest, "charles", ct: Ct);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Template);
        Assert.Equal("code-reviewer", result.Template.TemplateId);
        Assert.Equal(1, result.Template.Version);
        Assert.Equal(TalentTemplateStatus.Quarantined, result.Template.Status);
        Assert.NotEmpty(result.Template.ContentSha);
        Assert.Equal("charles", result.Template.SubmittedBy);
        Assert.Equal(_clock.GetUtcNow(), result.Template.CreatedAt);
    }

    [Theory]
    [InlineData("agents/alex/memory/notes.md")]
    [InlineData("Refer to memory/lessons/01.md for past failures")]
    [InlineData("Read hive/procedure/code-review.md")]
    [InlineData("Document stored in failures/crash.md")]
    public async Task Manifest_with_memory_file_references_is_rejected(string promptWithMemory)
    {
        var manifest = CreateValidManifest(prompt: promptWithMemory);
        var result = await _service.SubmitAsync(manifest, "agent", ct: Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("forbidden memory reference", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("anthropic:*")]
    [InlineData("auto")]
    [InlineData("auto/fast")]
    [InlineData("invalid_unqualified_model")]
    public async Task Manifest_with_invalid_model_whitelist_is_rejected(string model)
    {
        var manifest = CreateValidManifest() with { ModelWhitelist = [model] };
        var result = await _service.SubmitAsync(manifest, "agent", ct: Ct);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Manifest_with_secret_tokens_is_rejected()
    {
        // An API key pattern that sanitizer detects
        var promptWithSecret = "Use this key sk-ant-api03-abcdefghijklmnopqrstuvwxyz1234567890 for reviewing.";
        var manifest = CreateValidManifest(prompt: promptWithSecret);
        var result = await _service.SubmitAsync(manifest, "agent", ct: Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("secret patterns", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submitting_identical_manifest_is_rejected_as_duplicate()
    {
        var manifest = CreateValidManifest();
        var first = await _service.SubmitAsync(manifest, "agent", ct: Ct);
        Assert.True(first.Succeeded);

        var second = await _service.SubmitAsync(manifest, "agent", ct: Ct);
        Assert.False(second.Succeeded);
        Assert.Contains("identical content", second.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submitting_updated_manifest_increments_version()
    {
        var manifest1 = CreateValidManifest();
        var first = await _service.SubmitAsync(manifest1, "agent", ct: Ct);
        Assert.True(first.Succeeded);
        Assert.Equal(1, first.Template!.Version);

        _clock.Advance(TimeSpan.FromHours(1));
        var manifest2 = manifest1 with { Prompt = "Updated prompt for review v2 without memories." };
        var second = await _service.SubmitAsync(manifest2, "agent", ct: Ct);

        Assert.True(second.Succeeded);
        Assert.Equal(2, second.Template!.Version);
        Assert.NotEqual(first.Template.ContentSha, second.Template.ContentSha);

        var retrievedV1 = await _service.GetTemplateAsync("code-reviewer", 1, ct: Ct);
        Assert.NotNull(retrievedV1);
        Assert.Equal(1, retrievedV1.Version);

        var retrievedLatest = await _service.GetTemplateAsync("code-reviewer", ct: Ct);
        Assert.NotNull(retrievedLatest);
        Assert.Equal(2, retrievedLatest.Version);
    }

    [Fact]
    public async Task GetDiff_reports_manifest_changes_between_versions()
    {
        var manifest1 = CreateValidManifest();
        await _service.SubmitAsync(manifest1, "agent", ct: Ct);

        var manifest2 = manifest1 with
        {
            Name = "Lead Code Reviewer",
            ToolAllowlist = ["git.diff", "linter.exec"],
            ModelWhitelist = ["anthropic:claude-3-5-sonnet", "openai:o1-preview"],
            Prompt = "Enhanced prompt instructions without memory files.",
        };
        await _service.SubmitAsync(manifest2, "agent", ct: Ct);

        var diff = await _service.GetDiffAsync("code-reviewer", 1, 2, ct: Ct);

        Assert.Contains("Name: 'Senior Code Reviewer' -> 'Lead Code Reviewer'", diff);
        Assert.Contains("Tools Added: linter.exec", diff);
        Assert.Contains("Tools Removed: compiler.check", diff);
        Assert.Contains("Models Added: openai:o1-preview", diff);
        Assert.Contains("Models Removed: openai:gpt-4o", diff);
    }

    [Fact]
    public async Task Council_hire_motion_approval_activates_quarantined_template()
    {
        var manifest = CreateValidManifest();
        var submitResult = await _service.SubmitAsync(manifest, "agent", ct: Ct);
        Assert.True(submitResult.Succeeded);

        var outcome = new MembershipOutcome(
            VoteId: Guid.NewGuid(),
            SubjectAgentId: "agent-candidate",
            Kind: MembershipMotionKind.Hire,
            Decision: MembershipDecision.Approved,
            Choice: "yes",
            ReasonsSummaryRef: "gc_event:0192a000-0000-7000-8000-000000000001",
            DecidedAt: _clock.GetUtcNow(),
            VaultDecisionNo: 42);

        var hireResult = await _hireHook.OnMembershipOutcomeAsync(outcome, "code-reviewer", 1, ct: Ct);

        Assert.NotNull(hireResult);
        Assert.True(hireResult.Succeeded);
        Assert.NotNull(hireResult.Template);
        Assert.Equal(TalentTemplateStatus.Active, hireResult.Template.Status);
        Assert.Equal("Council", hireResult.Template.ApprovedBy);
        Assert.Equal(_clock.GetUtcNow(), hireResult.Template.ActivatedAt);
    }

    [Fact]
    public async Task Council_hire_motion_with_owner_approval_records_owner_approver()
    {
        var manifest = CreateValidManifest();
        await _service.SubmitAsync(manifest, "agent", ct: Ct);

        var outcome = new MembershipOutcome(
            VoteId: Guid.NewGuid(),
            SubjectAgentId: "agent-candidate",
            Kind: MembershipMotionKind.Hire,
            Decision: MembershipDecision.OwnerApproved,
            Choice: "yes",
            ReasonsSummaryRef: "gc_event:0192a000-0000-7000-8000-000000000001",
            DecidedAt: _clock.GetUtcNow(),
            VaultDecisionNo: 43);

        var hireResult = await _hireHook.OnMembershipOutcomeAsync(outcome, "code-reviewer", 1, ct: Ct);

        Assert.NotNull(hireResult);
        Assert.True(hireResult.Succeeded);
        Assert.Equal(TalentTemplateStatus.Active, hireResult.Template!.Status);
        Assert.Equal("Council+Owner", hireResult.Template.ApprovedBy);
    }

    [Fact]
    public async Task Council_hire_motion_rejection_does_not_activate_template()
    {
        var manifest = CreateValidManifest();
        await _service.SubmitAsync(manifest, "agent", ct: Ct);

        var outcome = new MembershipOutcome(
            VoteId: Guid.NewGuid(),
            SubjectAgentId: "agent-candidate",
            Kind: MembershipMotionKind.Hire,
            Decision: MembershipDecision.Rejected,
            Choice: "no",
            ReasonsSummaryRef: "gc_event:0192a000-0000-7000-8000-000000000001",
            DecidedAt: _clock.GetUtcNow(),
            VaultDecisionNo: null);

        var hireResult = await _hireHook.OnMembershipOutcomeAsync(outcome, "code-reviewer", 1, ct: Ct);

        Assert.NotNull(hireResult);
        Assert.False(hireResult.Succeeded);

        var template = await _service.GetTemplateAsync("code-reviewer", 1, ct: Ct);
        Assert.NotNull(template);
        Assert.Equal(TalentTemplateStatus.Quarantined, template.Status);
    }

    [Fact]
    public async Task ListTemplates_filters_by_status()
    {
        var m1 = CreateValidManifest("template-1");
        var m2 = CreateValidManifest("template-2");

        await _service.SubmitAsync(m1, "agent", ct: Ct);
        await _service.SubmitAsync(m2, "agent", ct: Ct);

        // Activate template-1
        await _service.ApplyHireMotionAsync("template-1", 1, "Council", ct: Ct);

        var all = await _service.ListTemplatesAsync(ct: Ct);
        Assert.Equal(2, all.Count);

        var active = await _service.ListTemplatesAsync(TalentTemplateStatus.Active, ct: Ct);
        Assert.Single(active);
        Assert.Equal("template-1", active[0].TemplateId);

        var quarantined = await _service.ListTemplatesAsync(TalentTemplateStatus.Quarantined, ct: Ct);
        Assert.Single(quarantined);
        Assert.Equal("template-2", quarantined[0].TemplateId);
    }
}
