using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.RedTeam;

/// <summary>
/// Replays the red-team corpus. Each attack class is a theory over its case ids; the invariant is that no Tier 2 or higher
/// tool call is allowed without an approval, whatever session it runs in and whatever fake grant is presented.
/// </summary>
public sealed class RedTeamReplayTests
{
    public static TheoryData<string> AttackClasses()
    {
        var data = new TheoryData<string>();
        foreach (var attackClass in RedTeamCorpus.AttackClasses)
        {
            data.Add(attackClass);
        }

        return data;
    }

    public static TheoryData<string> CaseIds(string attackClass)
    {
        var data = new TheoryData<string>();
        foreach (var testCase in RedTeamCorpus.ForClass(attackClass))
        {
            data.Add(testCase.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds), "hidden_unicode")]
    public void Hidden_unicode_cases_have_no_side_effect_without_approval(string caseId) => AssertNoSideEffectWithoutApproval(caseId);

    [Theory]
    [MemberData(nameof(CaseIds), "exfil_markdown")]
    public void Exfil_markdown_cases_have_no_side_effect_without_approval(string caseId) => AssertNoSideEffectWithoutApproval(caseId);

    [Theory]
    [MemberData(nameof(CaseIds), "malicious_rules_file")]
    public void Malicious_rules_file_cases_have_no_side_effect_without_approval(string caseId) => AssertNoSideEffectWithoutApproval(caseId);

    [Theory]
    [MemberData(nameof(CaseIds), "impersonating_gc_message")]
    public void Impersonating_gc_message_cases_have_no_side_effect_without_approval(string caseId) => AssertNoSideEffectWithoutApproval(caseId);

    [Theory]
    [MemberData(nameof(CaseIds), "typosquat_package")]
    public void Typosquat_package_cases_have_no_side_effect_without_approval(string caseId) => AssertNoSideEffectWithoutApproval(caseId);

    [Theory]
    [MemberData(nameof(CaseIds), "tool_description_poisoning")]
    public void Tool_description_poisoning_cases_have_no_side_effect_without_approval(string caseId) => AssertNoSideEffectWithoutApproval(caseId);

    [Theory]
    [MemberData(nameof(AttackClasses))]
    public void Each_attack_class_has_three_to_five_cases(string attackClass)
    {
        var count = RedTeamCorpus.ForClass(attackClass).Count;

        Assert.InRange(count, 3, 5);
    }

    [Fact]
    public void Case_ids_are_unique_and_each_case_sits_in_its_own_class_file()
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attackClass in RedTeamCorpus.AttackClasses)
        {
            foreach (var testCase in RedTeamCorpus.ForClass(attackClass))
            {
                if (!seen.Add(testCase.Id))
                {
                    failures.Add($"{testCase.Id}: id appears twice");
                }

                if (testCase.AttackClass != attackClass)
                {
                    failures.Add($"{testCase.Id}: class '{testCase.AttackClass}' is in file '{attackClass}'");
                }

                if (string.IsNullOrWhiteSpace(testCase.Description))
                {
                    failures.Add($"{testCase.Id}: description is blank");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Corpus_uses_only_obviously_synthetic_hosts()
    {
        var failures = new List<string>();
        foreach (var testCase in RedTeamCorpus.All)
        {
            var text = testCase.Content + " " + testCase.ToolCall.Args.GetRawText();
            foreach (Match match in Regex.Matches(text, @"https?://([A-Za-z0-9.-]+)"))
            {
                var host = match.Groups[1].Value;
                if (!host.EndsWith(".invalid", StringComparison.Ordinal))
                {
                    failures.Add($"{testCase.Id}: host '{host}' is not a .invalid name");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Positive_control_a_tier0_read_is_allowed_so_denials_mean_something()
    {
        var decision = Evaluate(RedTeamTools.VaultRead, SessionTaint.Clean with { State = TaintState.Hard }, SessionRoles.Actor, null, grant: null);

        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
    }

    [Fact]
    public void Positive_control_a_grant_with_the_exact_dispatch_hash_is_the_only_thing_that_allows_tier2()
    {
        var taint = new SessionTaint(TaintState.Hard, [new TaintSource(Guid.Parse("00000000-0000-0000-0000-0000000000aa"), ContentTrust.External, TaintState.Hard)]);
        var args = JsonElement.Parse("""{"branch":"main"}""");
        var altered = JsonElement.Parse("""{"branch":"release"}""");
        var exact = Context(RedTeamTools.RepoPush, taint, SessionRoles.Actor, "example.invalid/acme/ledger", args, grant: null);
        var hash = DispatchHash.Compute(exact);

        var matching = exact with { Grant = new VerifiedApproval(Guid.NewGuid(), RedTeamReplay.AgentId, RedTeamTools.RepoPush, hash, RedTeamReplay.Now.AddHours(1)) };
        var alteredCall = Context(RedTeamTools.RepoPush, taint, SessionRoles.Actor, "example.invalid/acme/ledger", altered, grant: null);
        var wrongArgs = alteredCall with { Grant = matching.Grant };

        var engine = new ToolPolicyEngine(RedTeamTools.Registry, new InMemoryRateBucketStore(), PolicyOptions.Default);

        Assert.Equal(PolicyOutcome.Allow, engine.Evaluate(matching).Outcome);
        Assert.NotEqual(PolicyOutcome.Allow, engine.Evaluate(wrongArgs).Outcome);
        Assert.NotEqual(PolicyOutcome.Allow, engine.Evaluate(exact with { Grant = null }).Outcome);
    }

    /// <summary>
    /// Shared invariant. Every session and grant combination must leave any Tier 2+ call without an allow. A failure names the case id.
    /// External content must also be marked as hard taint with its source recorded, and a planted secret must not survive sanitizing.
    /// </summary>
    private static void AssertNoSideEffectWithoutApproval(string caseId)
    {
        var testCase = RedTeamCorpus.Find(caseId);
        var replay = RedTeamReplay.Run(testCase);
        var failures = new List<string>();

        foreach (var run in replay.Runs)
        {
            if (run.ReachedTier is { } tier && tier >= ToolTier.OwnerApproves && run.Decision.Outcome == PolicyOutcome.Allow)
            {
                failures.Add($"{caseId}: {run.Session} session with {run.Grant} grant allowed {testCase.ToolCall.Tool} at {tier} ({run.Decision.Reason})");
            }
        }

        if (testCase.Trust == nameof(ContentTrust.External)
            && !(replay.Applied.State == TaintState.Hard && replay.Applied.Sources.Any(source => source.SourceId == replay.SourceId)))
        {
            failures.Add($"{caseId}: external content was not marked hard-tainted with its source recorded");
        }

        if (testCase.PlantedSecret is { } secret && replay.Sanitized.Text.Contains(secret, StringComparison.Ordinal))
        {
            failures.Add($"{caseId}: planted secret survived the sanitizer");
        }

        Assert.Empty(failures);
    }

    private static PolicyDecision Evaluate(string tool, SessionTaint taint, SessionRoles role, string? target, VerifiedApproval? grant)
    {
        var engine = new ToolPolicyEngine(RedTeamTools.Registry, new InMemoryRateBucketStore(), PolicyOptions.Default);
        var args = JsonElement.Parse("{}");

        return engine.Evaluate(Context(tool, taint, role, target, args, grant));
    }

    private static DispatchContext Context(string tool, SessionTaint taint, SessionRoles role, string? target, JsonElement args, VerifiedApproval? grant) =>
        new(RedTeamReplay.AgentId, Guid.Parse("00000000-0000-0000-0000-000000000012"), role, tool, null, target, args, taint, RedTeamReplay.Now, grant);
}
