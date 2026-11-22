using System.Reflection;
using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Client.Approvals;
using Bazaar.Client.Mobile;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.PullRequests;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;
using Bazaar.GitHub.Adapter;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Client.PullRequests.Tests;

public class PullRequestTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static (PullRequestSummaryViewModel Vm, FakePullRequestClient Client, FakeBiometric Biometric, FakeSigner Signer, FakeTimeProvider Clock)
        Make(PrListRow? row = null, AndroidSettings? settings = null, Action<FakeBiometric>? configure = null)
    {
        var client = new FakePullRequestClient();
        var biometric = new FakeBiometric();
        configure?.Invoke(biometric);
        var signer = new FakeSigner();
        var clock = new FakeTimeProvider(_start);
        var gate = new OwnerMergeGate(biometric, signer, Rig.Human, settings ?? Rig.Settings(), clock: clock);
        var vm = new PullRequestSummaryViewModel(row ?? Rig.Row(), client, gate, settings ?? Rig.Settings());
        return (vm, client, biometric, signer, clock);
    }

    private static OwnerMergeGate GateFor(FakeBiometric biometric, FakeSigner signer, FakeTimeProvider clock, AndroidSettings? settings = null) =>
        new(biometric, signer, Rig.Human, settings ?? Rig.Settings(), clock: clock);

    [Fact]
    public async Task Stale_head_voids_merge_and_refreshes()
    {
        var (vm, client, _, _, _) = Make();
        client.Summaries.Enqueue(Rig.Summary(Rig.HeadA));
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        vm.BeginMerge("squash");
        Assert.True(vm.HasPendingMerge);

        client.Merges.Enqueue(new PrMergeReply(PrMergeStatus.Stale, Rig.HeadB, "moved"));
        client.Summaries.Enqueue(Rig.Summary(Rig.HeadB));
        await vm.ConfirmMergeAsync(OwnerActionSource.Button, TestContext.Current.CancellationToken);

        Assert.False(vm.HasPendingMerge);
        Assert.Equal(Rig.HeadB, vm.HeadSha);
        Assert.Equal("The head moved. Approve again.", vm.Notice);
        Assert.False(vm.IsMerged);
    }

    [Fact]
    public async Task A_changed_head_on_reload_voids_a_waiting_merge()
    {
        var (vm, client, biometric, _, _) = Make();
        client.Summaries.Enqueue(Rig.Summary(Rig.HeadA));
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        vm.BeginMerge("squash");

        client.Summaries.Enqueue(Rig.Summary(Rig.HeadB));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.HasPendingMerge);
        await vm.ConfirmMergeAsync(OwnerActionSource.Button, TestContext.Current.CancellationToken);
        Assert.Equal(0, biometric.Prompts);
        Assert.Empty(client.MergeRequests);
    }

    [Fact]
    public async Task Failed_biometric_blocks_merge()
    {
        var (vm, client, biometric, signer, _) = Make(configure: b => b.Outcome = BiometricOutcome.Failed);
        client.Summaries.Enqueue(Rig.Summary());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        vm.BeginMerge("squash");

        await vm.ConfirmMergeAsync(OwnerActionSource.Button, TestContext.Current.CancellationToken);

        Assert.Equal(1, biometric.Prompts);
        Assert.Equal(0, signer.Signs);
        Assert.Empty(client.MergeRequests);
        Assert.Equal("The biometric check failed. The merge is blocked.", vm.Notice);
        Assert.False(vm.HasPendingMerge);
    }

    [Fact]
    public async Task Cancelled_or_unavailable_biometric_blocks_merge()
    {
        foreach (var outcome in new[] { BiometricOutcome.Cancelled, BiometricOutcome.Unavailable })
        {
            var biometric = new FakeBiometric { Outcome = outcome };
            var signer = new FakeSigner();
            var gate = GateFor(biometric, signer, new FakeTimeProvider(_start));
            var result = await gate.AuthorizeMergeAsync(Intent(), TestContext.Current.CancellationToken);

            Assert.Null(result.Approval);
            Assert.NotNull(result.Refusal);
            Assert.Equal(0, signer.Signs);
        }
    }

    [Fact]
    public async Task Approve_is_never_offered()
    {
        // No approve member on the view model, and the gate offers only merge.
        var members = typeof(PullRequestSummaryViewModel).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToArray();
        // ReviewerApproved is data; any member that acts as an approve is refused.
        Assert.DoesNotContain(members, name => name.Contains("Approve", StringComparison.OrdinalIgnoreCase) && !name.Contains("ReviewerApproved", StringComparison.Ordinal));

        // The wire client refuses an approve before anything is sent.
        var requests = new FakeWireRequester();
        var wire = new WirePullRequestClient(requests);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => wire.ReviewAsync(
            new PrReviewRequest("PR_1", Rig.HeadA, "approve", ""), TestContext.Current.CancellationToken));
        Assert.Contains("Approval is not a review event", ex.Message, StringComparison.Ordinal);
        Assert.Empty(requests.Types);

        // Review events the phone may send are comment and request-changes only.
        Assert.Equal(["comment", "request_changes"], [PrReviewEvents.Comment, PrReviewEvents.RequestChanges]);
    }

    [Fact]
    public async Task Merge_is_disabled_by_setting()
    {
        var settings = Rig.Settings(mergeFromFolded: false);
        var (vm, client, biometric, signer, clock) = Make(settings: settings);
        client.Summaries.Enqueue(Rig.Summary());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.CanMerge);
        vm.BeginMerge("squash");
        Assert.False(vm.HasPendingMerge);
        Assert.Equal("Merging is off on this phone.", vm.Notice);

        var direct = await GateFor(biometric, signer, clock, settings).AuthorizeMergeAsync(Intent(), TestContext.Current.CancellationToken);
        Assert.Equal(OwnerActionRefusal.MergeDisabled, direct.Refusal);
        Assert.Equal(0, biometric.Prompts);
        Assert.Empty(client.MergeRequests);
    }

    [Fact]
    public async Task Dictation_never_triggers_merge()
    {
        var (vm, client, biometric, signer, _) = Make();
        client.Summaries.Enqueue(Rig.Summary());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        vm.BeginMerge("squash");

        await vm.ConfirmMergeAsync(OwnerActionSource.Dictation, TestContext.Current.CancellationToken);

        Assert.Equal(0, biometric.Prompts);
        Assert.Equal(0, signer.Signs);
        Assert.Empty(client.MergeRequests);
        Assert.Equal("Merge needs a tap on the merge control.", vm.Notice);
    }

    [Fact]
    public async Task Signature_binds_the_head_sha()
    {
        var biometric = new FakeBiometric();
        using var signer = new FakeSigner();
        var gate = GateFor(biometric, signer, new FakeTimeProvider(_start));

        var result = await gate.AuthorizeMergeAsync(Intent(Rig.HeadA), TestContext.Current.CancellationToken);
        var signed = result.Approval!;
        var bytes = OwnerSignedPayload.Build(new OwnerPayload(
            signed.Payload.Action, signed.Payload.ApprovalId, signed.Payload.ArgsHash, signed.Payload.HeadSha,
            signed.Payload.HumanId, signed.Payload.DeviceId, signed.Payload.IssuedAt, signed.Payload.ExpiresAt, signed.Payload.Nonce));

        Assert.Equal(PrMergeArgs.Hash(Pr(), Rig.HeadA, "Add widget (#42)", ""), signed.Payload.ArgsHash);
        Assert.Equal(Rig.HeadA, signed.Payload.HeadSha);
        Assert.True(signer.PublicKey.VerifyData(bytes, signed.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        // The same signature over the same payload with another head sha does not verify.
        var moved = OwnerSignedPayload.Build(new OwnerPayload(
            signed.Payload.Action, signed.Payload.ApprovalId, signed.Payload.ArgsHash, Rig.HeadB,
            signed.Payload.HumanId, signed.Payload.DeviceId, signed.Payload.IssuedAt, signed.Payload.ExpiresAt, signed.Payload.Nonce));
        Assert.False(signer.PublicKey.VerifyData(moved, signed.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task Replay_of_a_signed_approval_is_refused()
    {
        var biometric = new FakeBiometric();
        using var signer = new FakeSigner();
        var gate = GateFor(biometric, signer, new FakeTimeProvider(_start));
        var signed = (await gate.AuthorizeMergeAsync(Intent(), TestContext.Current.CancellationToken)).Approval!;

        Assert.True(gate.TrySpend(signed));
        Assert.False(gate.TrySpend(signed));
    }

    [Fact]
    public async Task Every_merge_prompts_fresh_and_never_reuses_a_signature()
    {
        var (vm, client, biometric, _, _) = Make();
        client.Summaries.Enqueue(Rig.Summary());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.BeginMerge("squash");
        client.Merges.Enqueue(new PrMergeReply(PrMergeStatus.ChecksFailed, Rig.HeadA, "Checks are failing."));
        await vm.ConfirmMergeAsync(OwnerActionSource.Button, TestContext.Current.CancellationToken);

        vm.BeginMerge("squash");
        client.Merges.Enqueue(new PrMergeReply(PrMergeStatus.Merged, Rig.HeadA, null));
        await vm.ConfirmMergeAsync(OwnerActionSource.Button, TestContext.Current.CancellationToken);

        Assert.Equal(2, biometric.Prompts);
        Assert.Equal(2, client.MergeRequests.Count);
        Assert.NotEqual(client.MergeRequests[0].Owner.ApprovalId, client.MergeRequests[1].Owner.ApprovalId);
        Assert.NotEqual(client.MergeRequests[0].Owner.Nonce, client.MergeRequests[1].Owner.Nonce);
        Assert.True(vm.IsMerged);
    }

    [Fact]
    public async Task Biometric_grace_expires_before_signing()
    {
        var biometric = new FakeBiometric();
        using var signer = new FakeSigner();
        var clock = new FakeTimeProvider(_start);
        // The grace is measured from the biometric success; a signer that takes longer than 30 s is refused.
        var slow = new SlowSigner(signer, clock, TimeSpan.FromSeconds(31));
        var gate = new OwnerMergeGate(biometric, slow, Rig.Human, Rig.Settings(), clock: clock);

        var result = await gate.AuthorizeMergeAsync(Intent(), TestContext.Current.CancellationToken);

        Assert.Equal(OwnerActionRefusal.BiometricExpired, result.Refusal);
        Assert.Null(result.Approval);
    }

    [Fact]
    public async Task Signed_payload_expires_within_the_thirty_second_grace()
    {
        var biometric = new FakeBiometric();
        using var signer = new FakeSigner();
        var gate = GateFor(biometric, signer, new FakeTimeProvider(_start));

        var signed = (await gate.AuthorizeMergeAsync(Intent(), TestContext.Current.CancellationToken)).Approval!;

        Assert.Equal(TimeSpan.FromSeconds(30), signed.Payload.ExpiresAt - signed.Payload.IssuedAt);
    }

    [Fact]
    public async Task Summary_and_list_identity_come_from_the_server_fields()
    {
        var (vm, client, _, _, _) = Make(row: Rig.Row(withIdentity: false));
        client.Summaries.Enqueue(Rig.Summary(identity: true));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new PrIdentity(9001, "acme", "widgets", 42), vm.Identity);
        Assert.Null(PrIdentity.From(Rig.Row(withIdentity: false)));
        Assert.Null(PrIdentity.From(Rig.Summary(identity: false)));
    }

    [Fact]
    public async Task Merge_control_is_off_without_identity_or_on_failing_checks()
    {
        var (noIdentity, client, _, _, _) = Make(row: Rig.Row(withIdentity: false));
        client.Summaries.Enqueue(Rig.Summary(identity: false));
        await noIdentity.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(noIdentity.CanMerge);

        var (failing, client2, _, _, _) = Make(row: Rig.Row(checks: PrChecksRollup.Failing));
        client2.Summaries.Enqueue(Rig.Summary(checks: PrChecksRollup.Failing));
        await failing.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(failing.CanMerge);
    }

    [Fact]
    public async Task Full_diff_is_not_fetched_when_the_setting_is_off()
    {
        var (vm, client, _, _, _) = Make(settings: Rig.Settings(foldedFullDiff: false));
        client.Summaries.Enqueue(Rig.Summary());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.OpenDiffAsync("src/widget.cs", TestContext.Current.CancellationToken);

        Assert.Empty(client.HunksRequests);
        Assert.Empty(vm.DiffLines);
        Assert.Equal("The full diff is off on this phone.", vm.Notice);
    }

    [Fact]
    public async Task Diff_lines_come_from_the_hunks_at_the_loaded_head()
    {
        var (vm, client, _, _, _) = Make(settings: Rig.Settings(foldedFullDiff: true));
        client.Summaries.Enqueue(Rig.Summary());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var hunk = new PrHunk("@@ -1,2 +1,2 @@", 1, 2, 1, 2, [" keep", "-old", "+new"]);
        client.Hunks.Enqueue(new PrHunksReply(PrHunksStatus.Ok, Rig.HeadA, "src/widget.cs", [hunk], null));

        await vm.OpenDiffAsync("src/widget.cs", TestContext.Current.CancellationToken);

        Assert.Equal(Rig.HeadA, client.HunksRequests[0].HeadSha);
        Assert.Equal(
            [DiffLineKind.Header, DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added],
            vm.DiffLines.Select(l => l.Kind));
    }

    [Fact]
    public async Task Stale_hunks_void_the_merge_and_refresh()
    {
        var (vm, client, _, _, _) = Make(settings: Rig.Settings(foldedFullDiff: true));
        client.Summaries.Enqueue(Rig.Summary(Rig.HeadA));
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        vm.BeginMerge("squash");
        string? moved = null;
        vm.HeadMoved += (_, head) => moved = head;

        client.Hunks.Enqueue(new PrHunksReply(PrHunksStatus.Stale, Rig.HeadB, "src/widget.cs", [], null));
        client.Summaries.Enqueue(Rig.Summary(Rig.HeadB));
        await vm.OpenDiffAsync("src/widget.cs", TestContext.Current.CancellationToken);

        Assert.False(vm.HasPendingMerge);
        Assert.Equal(Rig.HeadB, moved);
        Assert.Equal(Rig.HeadB, vm.HeadSha);
        Assert.Empty(vm.DiffLines);
    }

    [Fact]
    public async Task Review_sends_comment_at_loaded_head()
    {
        var (vm, client, _, _, _) = Make();
        client.Summaries.Enqueue(Rig.Summary());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        client.Reviews.Enqueue(new PrReviewReply(PrReviewStatus.Applied, null, null));

        await vm.CommentAsync("looks fine", TestContext.Current.CancellationToken);

        Assert.Equal(PrReviewEvents.Comment, client.ReviewRequests[0].Event);
        Assert.Equal(Rig.HeadA, client.ReviewRequests[0].HeadSha);
        Assert.Equal("Review sent.", vm.Notice);
    }

    [Fact]
    public void Merge_args_hash_matches_the_server()
    {
        var pr = Pr();
        var phone = PrMergeArgs.Hash(pr, Rig.HeadA, "Add widget (#42)", "body");
        var server = OwnerMergeArgs.Hash(new PrRef(9001, "acme", "widgets", 42), new MergeRequest(Rig.HeadA, "Add widget (#42)", "body"));

        Assert.Equal(server, phone);
    }

    [Fact]
    public async Task List_reports_a_next_cursor_when_more_pages_exist()
    {
        var client = new FakePullRequestClient();
        client.Lists.Add(new PrListReply([Rig.Row()], "next"));
        var list = new PullRequestListViewModel(client);

        await list.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(list.Rows);
        Assert.True(list.HasMore);
    }

    [Fact]
    public async Task Bad_signature_reply_clears_the_pending_merge_and_is_not_resent()
    {
        var (vm, client, _, _, _) = Make();
        client.Summaries.Enqueue(Rig.Summary());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        vm.BeginMerge("squash");
        client.Merges.Enqueue(new PrMergeReply(PrMergeStatus.BadSignature, null, null));

        await vm.ConfirmMergeAsync(OwnerActionSource.Button, TestContext.Current.CancellationToken);

        Assert.Single(client.MergeRequests);
        Assert.False(vm.HasPendingMerge);
        Assert.Equal("The owner signature was refused. Approve again.", vm.Notice);
    }

    private static PrIdentity Pr() => new(9001, "acme", "widgets", 42);

    private static OwnerMergeIntent Intent(string head = Rig.HeadA) =>
        new(OwnerActionSource.Button, Pr(), head, "Add widget (#42)", "");

    /// <summary>Advances the fake clock while signing, to exercise the grace period.</summary>
    private sealed class SlowSigner(FakeSigner inner, FakeTimeProvider clock, TimeSpan delay) : IDeviceApprovalSigner
    {
        public string DeviceId => inner.DeviceId;

        public async ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
        {
            clock.Advance(delay);
            return await inner.SignAsync(canonicalPayload, cancellationToken).ConfigureAwait(false);
        }
    }
}
