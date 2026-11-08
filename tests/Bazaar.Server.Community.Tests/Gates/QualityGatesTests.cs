using Bazaar.Server.Community.Gates;

namespace Bazaar.Server.Community.Tests.Gates;

public class QualityGatesTests
{
    private static readonly DateOnly _today = new(2026, 10, 30);

    private static readonly IReadOnlyList<string> _defaultQueries = ["bazaar crash on startup"];
    private static readonly IReadOnlyList<string> _defaultLinks = ["https://example.org/issues/1"];

    private static DraftPost GoodDraft() => new(
        Platform: "example",
        Kind: "comment",
        Body: "Same question as the linked thread; a short answer follows. Posted by an AI agent (Bazaar), sponsor <name>, contact <address>.",
        DuplicateSearchQueries: _defaultQueries,
        RelatedLinks: _defaultLinks,
        ExampleOutput: null,
        ExampleVersions: null,
        IsBugReport: false,
        DuplicateSearchLinksChecked: _defaultLinks);

    private static PlatformPolicy GoodPolicy(DateOnly? checkedOn = null, PolicyStatus status = PolicyStatus.Verified, string disclosure = "Assisted-by trailer") => new(
        Platform: "example",
        Status: status,
        Allowed: new HashSet<CommunityAction> { CommunityAction.Read, CommunityAction.ReplyApprovedThread },
        DisclosureRule: disclosure,
        SourceUrl: new Uri("https://example.org/contributing"),
        CheckedOn: checkedOn ?? _today.AddDays(-1));

    private static bool HasGate(GateResult result, string gate) => result.Failures.Any(f => f.Gate == gate);

    [Fact]
    public void Clean_draft_with_verified_fresh_policy_passes()
    {
        GateResult result = QualityGates.Evaluate(GoodDraft(), GoodPolicy(), _today);

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Missing_duplicate_search_queries_fail_duplicate_search()
    {
        DraftPost draft = GoodDraft() with { DuplicateSearchQueries = [] };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.False(result.Passed);
        Assert.True(HasGate(result, "duplicate_search"));
    }

    [Fact]
    public void Missing_checked_links_fail_duplicate_search()
    {
        DraftPost draft = GoodDraft() with { DuplicateSearchLinksChecked = [] };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.True(HasGate(result, "duplicate_search"));
    }

    [Fact]
    public void Bug_report_without_example_output_fails_example_evidence()
    {
        DraftPost draft = GoodDraft() with { IsBugReport = true, ExampleOutput = null, ExampleVersions = "tool 2.0" };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.True(HasGate(result, "example_evidence"));
    }

    [Fact]
    public void Bug_report_without_versions_fails_example_evidence()
    {
        DraftPost draft = GoodDraft() with { IsBugReport = true, ExampleOutput = "exit code 1", ExampleVersions = "  " };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.True(HasGate(result, "example_evidence"));
    }

    [Fact]
    public void Bug_report_with_example_and_versions_passes_example_gate()
    {
        DraftPost draft = GoodDraft() with { IsBugReport = true, ExampleOutput = "exit code 1", ExampleVersions = "tool 2.0" };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Non_bug_report_needs_no_example()
    {
        DraftPost draft = GoodDraft() with { IsBugReport = false, ExampleOutput = null, ExampleVersions = null };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.False(HasGate(result, "example_evidence"));
    }

    [Fact]
    public void Policy_checked_31_days_ago_fails_rule_freshness()
    {
        GateResult result = QualityGates.Evaluate(GoodDraft(), GoodPolicy(checkedOn: _today.AddDays(-31)), _today);

        Assert.True(HasGate(result, "rule_freshness"));
    }

    [Fact]
    public void Policy_checked_exactly_30_days_ago_is_still_fresh()
    {
        GateResult result = QualityGates.Evaluate(GoodDraft(), GoodPolicy(checkedOn: _today.AddDays(-30)), _today);

        Assert.False(HasGate(result, "rule_freshness"));
    }

    [Fact]
    public void Null_policy_fails_rule_freshness()
    {
        GateResult result = QualityGates.Evaluate(GoodDraft(), null, _today);

        Assert.False(result.Passed);
        Assert.True(HasGate(result, "rule_freshness"));
    }

    [Fact]
    public void Unverified_policy_fails_rule_unverified()
    {
        GateResult result = QualityGates.Evaluate(GoodDraft(), GoodPolicy(status: PolicyStatus.Secondary), _today);

        Assert.True(HasGate(result, "rule_unverified"));
    }

    [Fact]
    public void Disclosure_rule_with_ai_generated_wording_fails_ai_contribution_bar()
    {
        GateResult result = QualityGates.Evaluate(
            GoodDraft(),
            GoodPolicy(disclosure: "No generated or AI-edited text"),
            _today);

        Assert.True(HasGate(result, "ai_contribution_bar"));
    }

    [Fact]
    public void Disclosure_rule_with_no_ai_wording_fails_ai_contribution_bar()
    {
        GateResult result = QualityGates.Evaluate(GoodDraft(), GoodPolicy(disclosure: "Posts by NO AI accounts"), _today);

        Assert.True(HasGate(result, "ai_contribution_bar"));
    }

    [Fact]
    public void Email_in_body_fails_personal_data()
    {
        DraftPost draft = GoodDraft() with { Body = "Reach me at jane.doe@example.com for details." };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.True(HasGate(result, "personal_data"));
    }

    [Fact]
    public void Phone_number_with_separators_fails_personal_data()
    {
        DraftPost draft = GoodDraft() with { Body = "Call +44 20 7946 0958 about the build." };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.True(HasGate(result, "personal_data"));
    }

    [Fact]
    public void Token_like_string_fails_personal_data()
    {
        DraftPost draft = GoodDraft() with { Body = "The config had ghp_xxxxxxxxxxxxxxxxxxxxxxxxxxxx in it." };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.True(HasGate(result, "personal_data"));
    }

    [Fact]
    public void Localhost_link_fails_internal_link()
    {
        DraftPost draft = GoodDraft() with { Body = "Repro at http://localhost:8080/api/status today." };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.True(HasGate(result, "internal_link"));
    }

    [Fact]
    public void Version_numbers_and_dates_are_not_flagged()
    {
        DraftPost draft = GoodDraft() with { Body = "Tested on 10.0.100 and 2026-10-30 with 1.2.3 and 555-1234." };

        GateResult result = QualityGates.Evaluate(draft, GoodPolicy(), _today);

        Assert.False(HasGate(result, "personal_data"));
        Assert.False(HasGate(result, "internal_link"));
    }

    [Fact]
    public void All_failures_are_reported_together()
    {
        DraftPost draft = GoodDraft() with
        {
            DuplicateSearchQueries = [],
            IsBugReport = true,
            ExampleOutput = null,
            ExampleVersions = null,
            Body = "Mail jane.doe@example.com; see http://127.0.0.1:5000.",
        };

        GateResult result = QualityGates.Evaluate(draft, null, _today);

        Assert.False(result.Passed);
        Assert.True(HasGate(result, "duplicate_search"));
        Assert.True(HasGate(result, "example_evidence"));
        Assert.True(HasGate(result, "rule_freshness"));
        Assert.True(HasGate(result, "personal_data"));
        Assert.True(HasGate(result, "internal_link"));
        Assert.Equal(5, result.Failures.Count);
    }
}
