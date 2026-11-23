using System.Security.Cryptography;
using Bazaar.Community.Drafts;
using Bazaar.Contracts.Approvals;
using Bazaar.Server.Community;

namespace Bazaar.Community.Tests.Drafts;

public class CommunityDraftDecisionHandlerTests
{
    private static Task<DraftDecisionResult> Run(DraftFixture f, Bazaar.Contracts.Community.DraftReviewDecisionDto dto) =>
        f.Handler.HandleAsync(dto.DraftId, dto, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Approve_posts_once_records_the_receipt_and_is_idempotent_on_replay()
    {
        using var f = new DraftFixture();
        var dto = f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce);

        var first = await Run(f, dto);
        var replay = await Run(f, dto);

        Assert.Equal(DraftDecisionStatus.Ok, first.Status);
        Assert.Equal("posted", first.Result!.Outcome);
        Assert.Equal("ext-42", first.Result.ExternalId);
        Assert.False(first.Result.AlreadyDecided);
        Assert.Equal(DraftDecisionStatus.Ok, replay.Status);
        Assert.True(replay.Result!.AlreadyDecided);
        Assert.Equal("ext-42", replay.Result.ExternalId);

        var posted = Assert.Single(f.Adapter.Posts);
        Assert.Equal(f.Draft.Post, posted.Post);
        Assert.Equal(f.Draft.ApprovalId, posted.Approval.Value);
        var row = Assert.Single(f.Sql.Rows);
        Assert.Equal("done", row.Status);
        Assert.Equal("ext-42", row.ExternalId);
        Assert.Equal(f.Draft.ApprovalId.ToString("D"), row.ApprovalId);
        Assert.Equal(CommunityDraftStatus.Posted, f.Store.All.Single().Status);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Payload.Contains("Crash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Body_hash_mismatch_denies_and_never_posts()
    {
        using var f = new DraftFixture();
        var tampered = f.Draft with { Post = f.Draft.Post with { Body = f.Draft.Post.Body + " Click here." } };
        f.Store.Add(tampered);

        var result = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce));

        Assert.Equal(DraftDecisionStatus.Ok, result.Status);
        Assert.Equal("denied", result.Result!.Outcome);
        Assert.Equal("body_hash_mismatch", result.Result.Reason);
        Assert.Empty(f.Adapter.Posts);
        Assert.Equal("denied_body_hash_mismatch", Assert.Single(f.Sql.Rows).Status);
        Assert.Equal(CommunityDraftStatus.Denied, f.Store.All.Single().Status);
    }

    [Fact]
    public async Task Failed_gates_deny_even_with_a_valid_approve_signature()
    {
        using var f = new DraftFixture(gatesPass: false);

        var result = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce));

        Assert.Equal("denied", result.Result!.Outcome);
        Assert.Equal("gates_failed", result.Result.Reason);
        Assert.Empty(f.Adapter.Posts);
    }

    [Fact]
    public async Task Bad_signature_is_forbidden_and_changes_nothing()
    {
        using var f = new DraftFixture();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce, key: stranger);
        // A deny signature replayed as an approve must fail too: the signed bit differs.
        var wrongBit = f.Sign(f.Draft, ApprovalDecisionKind.Deny) with { Decision = f.Sign(f.Draft, ApprovalDecisionKind.Deny).Decision with { Decision = ApprovalDecisionKind.ApproveOnce } };
        var unknownDevice = f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce, deviceId: "other-device");

        foreach (var dto in new[] { forged, wrongBit, unknownDevice })
        {
            var result = await Run(f, dto);
            Assert.Equal(DraftDecisionStatus.Forbidden, result.Status);
            Assert.Equal("invalid_signature", result.Code);
        }

        Assert.Empty(f.Adapter.Posts);
        Assert.Empty(f.Sql.Rows);
        Assert.Equal(CommunityDraftStatus.Pending, f.Store.All.Single().Status);
    }

    [Fact]
    public async Task Deny_records_a_denial_and_replays_idempotently_but_cannot_flip_to_approve()
    {
        using var f = new DraftFixture();

        var denied = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.Deny));
        var replay = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.Deny));
        var flip = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce));

        Assert.Equal("denied", denied.Result!.Outcome);
        Assert.False(denied.Result.AlreadyDecided);
        Assert.True(replay.Result!.AlreadyDecided);
        Assert.Equal(DraftDecisionStatus.Conflict, flip.Status);
        Assert.Empty(f.Adapter.Posts);
        Assert.Equal("owner_denied", Assert.Single(f.Sql.Rows).Status);
    }

    [Fact]
    public async Task Edit_creates_a_new_pending_draft_with_footer_and_never_posts()
    {
        using var f = new DraftFixture();

        var result = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.EditThenApprove, edited: "Reworded and shorter."));

        Assert.Equal(DraftDecisionStatus.Ok, result.Status);
        Assert.Equal("edited", result.Result!.Outcome);
        var card = result.Result.NewDraft!;
        Assert.NotEqual(f.Draft.DraftId, card.DraftId);
        Assert.NotEqual(f.Draft.ApprovalId, card.Approval.ApprovalId);
        Assert.StartsWith("Reworded and shorter.", card.Body, StringComparison.Ordinal);
        Assert.EndsWith("contact sam@example.test.", card.Body, StringComparison.Ordinal);
        Assert.True(card.FooterPresent);
        Assert.Equal(DraftApprovalHash.BodySha256Hex(card.Body), card.BodySha256);
        Assert.Equal(ApprovalCardStatus.Pending, card.Approval.Status);
        Assert.Empty(f.Adapter.Posts);

        var original = await f.Store.GetAsync(f.Draft.DraftId, TestContext.Current.CancellationToken);
        Assert.Equal(CommunityDraftStatus.Edited, original!.Status);
        var created = await f.Store.GetAsync(card.DraftId, TestContext.Current.CancellationToken);
        Assert.Equal(CommunityDraftStatus.Pending, created!.Status);
        Assert.True(created.ApprovalExpiresAt > f.Draft.ApprovalExpiresAt - TimeSpan.FromDays(1));
        Assert.Equal("denied_edited", Assert.Single(f.Sql.Rows).Status);

        // The old approval cannot post the edited text, and a repeated edit returns the same new draft.
        var oldApprove = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce));
        Assert.Equal(DraftDecisionStatus.Conflict, oldApprove.Status);
        var replay = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.EditThenApprove, edited: "Reworded and shorter."));
        Assert.True(replay.Result!.AlreadyDecided);
        Assert.Equal(card.DraftId, replay.Result.NewDraft!.DraftId);
        Assert.Equal(2, f.Store.All.Count);
    }

    [Fact]
    public async Task Edit_reruns_the_gates_and_a_secret_in_the_edit_blocks_approval_of_the_new_draft()
    {
        using var f = new DraftFixture();
        var edit = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.EditThenApprove, edited: "See https://10.0.0.5/internal and mail someone@example.com"));

        var card = edit.Result!.NewDraft!;

        Assert.NotEmpty(card.Gates);
        Assert.DoesNotContain(ApprovalDecisionKind.ApproveOnce, card.Approval.AvailableDecisions);
        var created = (await f.Store.GetAsync(card.DraftId, TestContext.Current.CancellationToken))!;
        var approve = await Run(f, f.Sign(created, ApprovalDecisionKind.ApproveOnce));
        Assert.Equal("gates_failed", approve.Result!.Reason);
        Assert.Empty(f.Adapter.Posts);
    }

    [Fact]
    public async Task Edited_draft_can_then_be_approved_under_its_own_approval()
    {
        using var f = new DraftFixture();
        var edit = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.EditThenApprove, edited: "Reworded and shorter."));
        var created = (await f.Store.GetAsync(edit.Result!.NewDraft!.DraftId, TestContext.Current.CancellationToken))!;

        var posted = await Run(f, f.Sign(created, ApprovalDecisionKind.ApproveOnce));

        Assert.Equal("posted", posted.Result!.Outcome);
        Assert.Equal(created.Post.Body, Assert.Single(f.Adapter.Posts).Post.Body);
    }

    [Fact]
    public async Task Expired_pending_draft_is_a_conflict_and_never_posts()
    {
        using var f = new DraftFixture();
        f.Clock.Now = f.Draft.ApprovalExpiresAt;

        var result = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce));

        Assert.Equal(DraftDecisionStatus.Conflict, result.Status);
        Assert.Equal("expired", result.Code);
        Assert.Empty(f.Adapter.Posts);
        Assert.Equal(CommunityDraftStatus.Expired, f.Store.All.Single().Status);
    }

    [Fact]
    public async Task Retryable_adapter_failure_leaves_the_draft_pending_and_a_retry_posts_once()
    {
        using var f = new DraftFixture();
        f.Adapter.Failures.Enqueue(new CommunityPostException("rate limited", retryable: true));
        var dto = f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce);

        var first = await Run(f, dto);

        Assert.Equal(DraftDecisionStatus.Unavailable, first.Status);
        Assert.Equal(CommunityDraftStatus.Pending, f.Store.All.Single().Status);
        Assert.Empty(f.Sql.Rows);

        var retry = await Run(f, dto);

        Assert.Equal("posted", retry.Result!.Outcome);
        Assert.Single(f.Adapter.Posts);
        Assert.Single(f.Sql.Rows);
    }

    [Fact]
    public async Task Permanent_adapter_refusal_fails_the_draft()
    {
        using var f = new DraftFixture();
        f.Adapter.Failures.Enqueue(new CommunityPostException("forbidden", retryable: false));

        var result = await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce));

        Assert.Equal(DraftDecisionStatus.PostRefused, result.Status);
        Assert.Equal(CommunityDraftStatus.Failed, f.Store.All.Single().Status);
        Assert.Equal("failed", Assert.Single(f.Sql.Rows).Status);
    }

    [Fact]
    public async Task Missing_adapter_is_unavailable_and_the_draft_stays_pending()
    {
        using var f = new DraftFixture();
        var other = f.NewDraft(true) with { Post = f.Draft.Post with { Platform = "reddit" } };
        other = other with { ApprovalHash = DraftApprovalHash.Compute(other.Post) };
        f.Store.Add(other);

        var result = await Run(f, f.Sign(other, ApprovalDecisionKind.ApproveOnce));

        Assert.Equal(DraftDecisionStatus.Unavailable, result.Status);
        Assert.Equal("no_adapter", result.Code);
        Assert.Equal(CommunityDraftStatus.Pending, (await f.Store.GetAsync(other.DraftId, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task Unknown_draft_is_404_and_malformed_decisions_are_400()
    {
        using var f = new DraftFixture();
        var good = f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce);
        var unknown = Guid.NewGuid();

        Assert.Equal(DraftDecisionStatus.NotFound, (await f.Handler.HandleAsync(unknown, good with { DraftId = unknown }, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(DraftDecisionStatus.BadRequest, (await f.Handler.HandleAsync(f.Draft.DraftId, null, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(DraftDecisionStatus.BadRequest, (await f.Handler.HandleAsync(Guid.NewGuid(), good, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(DraftDecisionStatus.BadRequest, (await Run(f, good with { Decision = good.Decision with { SignatureBase64 = null } })).Status);
        Assert.Equal(DraftDecisionStatus.BadRequest, (await Run(f, good with { Decision = good.Decision with { SignatureBase64 = "AAAA" } })).Status);
        Assert.Equal(DraftDecisionStatus.BadRequest, (await Run(f, good with { Decision = good.Decision with { Decision = ApprovalDecisionKind.ApproveForTask } })).Status);
        Assert.Equal(DraftDecisionStatus.BadRequest, (await Run(f, good with { Decision = good.Decision with { ApprovalId = Guid.NewGuid() } })).Status);
        Assert.Equal(DraftDecisionStatus.BadRequest, (await Run(f, f.Sign(f.Draft, ApprovalDecisionKind.EditThenApprove, edited: "  "))).Status);
        Assert.Empty(f.Adapter.Posts);
    }

    [Fact]
    public void Approval_hash_covers_body_target_and_title()
    {
        using var f = new DraftFixture();
        var baseline = DraftApprovalHash.Compute(f.Draft.Post);

        Assert.NotEqual(baseline, DraftApprovalHash.Compute(f.Draft.Post with { Body = "x" }));
        Assert.NotEqual(baseline, DraftApprovalHash.Compute(f.Draft.Post with { Target = "other/repo" }));
        Assert.NotEqual(baseline, DraftApprovalHash.Compute(f.Draft.Post with { Title = "Another" }));
        Assert.Equal(baseline, DraftApprovalHash.Compute(f.Draft.Post with { }));
    }
}
