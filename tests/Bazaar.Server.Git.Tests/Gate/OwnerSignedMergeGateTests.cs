using Bazaar.Approvals;
using Bazaar.Server.Git.Forge;
using Bazaar.Server.Git.Merge;

namespace Bazaar.Server.Git.Tests.Gate;

public class OwnerSignedMergeGateTests
{
    private readonly FakeForge _forge = new();
    private readonly FakeExecutor _exec = new();
    private readonly FakeVerifier _verifier = new();
    private readonly FakeAudit _audit = new();

    private OwnerSignedMergeGate Gate(bool green = true) =>
        new(new ForgeRegistry([_forge], [_exec]), _verifier, _audit, new GitMergeOptions { RequireGreenChecks = green });

    private async Task<MergeNotApprovedException> Refused(OwnerMergeApproval approval, MergeRequest merge, string code, bool green = true)
    {
        var ex = await Assert.ThrowsAsync<MergeNotApprovedException>(() => Gate(green).MergeAsync(approval, merge, TestContext.Current.CancellationToken));
        Assert.Equal(code, ex.Code);
        Assert.Equal(0, _exec.Calls);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(OwnerSignedMergeGate.RefusedKind, entry.Kind);
        Assert.Contains(code, entry.Payload, StringComparison.Ordinal);
        return ex;
    }

    [Fact]
    public async Task Happy_path_merges_once_and_audits()
    {
        await Gate().MergeAsync(TestData.Approval(), TestData.Merge(), TestContext.Current.CancellationToken);

        Assert.Equal(1, _exec.Calls);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(OwnerSignedMergeGate.MergedKind, entry.Kind);
        Assert.Contains(TestData.Sha, entry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Head_sha_comparison_ignores_case()
    {
        var merge = TestData.Merge(TestData.Sha.ToUpperInvariant());
        await Gate().MergeAsync(TestData.Approval(merge), merge, TestContext.Current.CancellationToken);
        Assert.Equal(1, _exec.Calls);
    }

    [Fact]
    public Task Different_head_shas_refuse() =>
        Refused(TestData.Approval(headSha: TestData.OtherSha), TestData.Merge(), "head_mismatch");

    [Fact]
    public Task Approval_without_head_sha_refuses() =>
        Refused(TestData.Approval(headSha: null), TestData.Merge(), "head_mismatch");

    [Fact]
    public Task Wrong_action_refuses() =>
        Refused(TestData.Approval(action: "legal_hold.release"), TestData.Merge(), "wrong_action");

    [Fact]
    public Task Wrong_args_hash_refuses() =>
        Refused(TestData.Approval(argsHash: [9, 9, 9]), TestData.Merge(), "args_mismatch");

    [Fact]
    public Task Approval_for_a_different_commit_title_refuses()
    {
        var approved = TestData.Merge() with { CommitTitle = "something else" };
        return Refused(TestData.Approval(approved), TestData.Merge(), "args_mismatch");
    }

    [Theory]
    [InlineData(OwnerSignatureVerdict.BadSignature)]
    [InlineData(OwnerSignatureVerdict.Expired)]
    [InlineData(OwnerSignatureVerdict.NonceReplayed)]
    [InlineData(OwnerSignatureVerdict.NotTheOwner)]
    public async Task Any_verdict_but_valid_refuses(OwnerSignatureVerdict verdict)
    {
        _verifier.Verdict = verdict;
        await Refused(TestData.Approval(), TestData.Merge(), "bad_signature");
    }

    [Fact]
    public async Task Stale_forge_head_refuses()
    {
        _forge.Head = TestData.OtherSha;
        await Refused(TestData.Approval(), TestData.Merge(), "head_moved");
    }

    [Fact]
    public async Task Failed_check_refuses()
    {
        _forge.Checks = [new CheckRun("ci", CheckState.Failure, TestData.Sha, null)];
        await Refused(TestData.Approval(), TestData.Merge(), "checks_not_green");
    }

    [Fact]
    public async Task Pending_check_refuses()
    {
        _forge.Checks = [new CheckRun("ok", CheckState.Success, TestData.Sha, null), new CheckRun("slow", CheckState.Pending, TestData.Sha, null)];
        await Refused(TestData.Approval(), TestData.Merge(), "checks_not_green");
    }

    [Fact]
    public async Task Check_on_another_head_refuses()
    {
        _forge.Checks = [new CheckRun("ci", CheckState.Success, TestData.OtherSha, null)];
        await Refused(TestData.Approval(), TestData.Merge(), "checks_not_green");
    }

    [Fact]
    public async Task Skipped_and_success_checks_pass()
    {
        _forge.Checks = [new CheckRun("a", CheckState.Skipped, TestData.Sha, null), new CheckRun("b", CheckState.Success, TestData.Sha, null)];
        await Gate().MergeAsync(TestData.Approval(), TestData.Merge(), TestContext.Current.CancellationToken);
        Assert.Equal(1, _exec.Calls);
    }

    [Fact]
    public async Task Green_checks_option_off_merges_despite_failing_check()
    {
        _forge.Checks = [new CheckRun("ci", CheckState.Failure, TestData.Sha, null)];
        await Gate(green: false).MergeAsync(TestData.Approval(), TestData.Merge(), TestContext.Current.CancellationToken);
        Assert.Equal(1, _exec.Calls);
    }

    [Fact]
    public async Task Green_checks_option_off_still_enforces_the_other_checks()
    {
        _forge.Head = TestData.OtherSha;
        await Refused(TestData.Approval(), TestData.Merge(), "head_moved", green: false);
    }

    [Fact]
    public async Task Audit_failure_after_a_real_merge_does_not_throw()
    {
        _audit.Throws = true;
        await Gate().MergeAsync(TestData.Approval(), TestData.Merge(), TestContext.Current.CancellationToken);
        Assert.Equal(1, _exec.Calls);
    }

    [Fact]
    public async Task Audit_failure_on_a_refusal_still_refuses_with_no_merge()
    {
        _audit.Throws = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Gate().MergeAsync(TestData.Approval(headSha: TestData.OtherSha), TestData.Merge(), TestContext.Current.CancellationToken));
        Assert.Equal(0, _exec.Calls);
    }

    [Fact]
    public async Task Unknown_provider_is_audited_as_failed_and_never_merges()
    {
        var pr = new PrRef("nowhere", "a/b", 1);
        var merge = TestData.Merge();
        var approval = TestData.Approval(merge) with { Pr = pr };
        approval = approval with { Payload = approval.Payload with { ArgsHash = GitMergeArgs.Hash(pr, merge) } };

        await Assert.ThrowsAsync<UnknownForgeProviderException>(() => Gate().MergeAsync(approval, merge, TestContext.Current.CancellationToken));

        Assert.Equal(0, _exec.Calls);
        Assert.Equal(OwnerSignedMergeGate.FailedKind, Assert.Single(_audit.Entries).Kind);
    }

    [Fact]
    public void Args_hash_binds_provider_repo_number_and_head()
    {
        var m = TestData.Merge();
        var baseline = GitMergeArgs.Hash(TestData.Pr, m);
        Assert.NotEqual(baseline, GitMergeArgs.Hash(TestData.Pr with { Number = 8 }, m));
        Assert.NotEqual(baseline, GitMergeArgs.Hash(TestData.Pr with { Provider = "other" }, m));
        Assert.NotEqual(baseline, GitMergeArgs.Hash(TestData.Pr with { FullName = "acme/other" }, m));
        Assert.Equal(baseline, GitMergeArgs.Hash(TestData.Pr, m with { HeadSha = m.HeadSha.ToUpperInvariant() }));
    }
}
