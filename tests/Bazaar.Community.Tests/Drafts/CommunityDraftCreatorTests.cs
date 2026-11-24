using Bazaar.Community.Drafts;
using Bazaar.Community.Gates;
using Bazaar.Community.Profile;
using Bazaar.Contracts.Approvals;

namespace Bazaar.Community.Tests.Drafts;

/// <summary>The draft creator: footer, gate input without the footer, approval hash, expiry and validation.</summary>
public class CommunityDraftCreatorTests
{
    private const string _text = "Found a crash on start; steps below.";

    private static CommunityDraftProposal Proposal(bool gatesPass = true, string text = _text, CommunityAction action = CommunityAction.NewIssue) => new(
        "agent-1", "sponsor-1", action, DraftFixture.Profile,
        new DraftPost("github", "issue", text, gatesPass ? ["crash on start"] : [], [], null, null, false, gatesPass ? ["https://example.org/issues/1"] : []),
        "owner/repo", "Crash on start");

    private static CommunityDraftCreator Creator(DraftFixture f, CommunityDraftOptions? options = null) =>
        new(f.Store, new FakePolicySource(f.Policy), f.Handler, f.Disclosure, f.Clock, options);

    [Fact]
    public async Task Creates_a_pending_draft_with_the_footer_hash_and_expiry()
    {
        using var f = new DraftFixture();

        var result = await Creator(f).CreateAsync(Proposal(), TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        var card = result.Card!;
        var stored = f.Store.All.Single(d => d.DraftId == card.DraftId);
        Assert.Equal(CommunityDraftStatus.Pending, stored.Status);
        Assert.StartsWith(_text, stored.Post.Body, StringComparison.Ordinal);
        Assert.EndsWith(BotProfileBuilder.Footer(DraftFixture.Profile, f.Disclosure), stored.Post.Body, StringComparison.Ordinal);
        Assert.Equal(DraftApprovalHash.Compute(stored.Post), stored.ApprovalHash);
        Assert.Equal(DraftApprovalHash.BodySha256Hex(stored.Post.Body), card.BodySha256);
        Assert.Equal(DraftFixture.Start.AddHours(24), stored.ApprovalExpiresAt);
        Assert.Equal(_text, stored.GateInput.Body);
        Assert.True(card.FooterPresent);
        Assert.Empty(card.Gates);
        Assert.Contains(ApprovalDecisionKind.ApproveOnce, card.Approval.AvailableDecisions);
    }

    [Fact]
    public async Task Gates_read_the_text_without_the_footer_so_the_sponsor_contact_does_not_trip_personal_data()
    {
        using var f = new DraftFixture();

        var result = await Creator(f).CreateAsync(Proposal(), TestContext.Current.CancellationToken);

        Assert.Contains("sam@example.test", result.Card!.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Card.Gates, g => g.Gate == "personal_data");
    }

    [Fact]
    public async Task An_email_in_the_agent_text_still_fails_personal_data_and_disables_approve()
    {
        using var f = new DraftFixture();

        var result = await Creator(f).CreateAsync(Proposal(text: "Reach me at someone@example.test about this."), TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.Contains(result.Card!.Gates, g => g.Gate == "personal_data");
        Assert.DoesNotContain(ApprovalDecisionKind.ApproveOnce, result.Card.Approval.AvailableDecisions);
    }

    [Fact]
    public async Task Failed_gates_are_stored_so_the_owner_can_edit_or_deny()
    {
        using var f = new DraftFixture();

        var result = await Creator(f).CreateAsync(Proposal(gatesPass: false), TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.Contains(result.Card!.Gates, g => g.Gate == "duplicate_search");
    }

    [Fact]
    public async Task Approving_a_created_draft_posts_it_through_the_handler()
    {
        using var f = new DraftFixture();
        var created = await Creator(f).CreateAsync(Proposal(), TestContext.Current.CancellationToken);
        var draft = f.Store.All.Single(d => d.DraftId == created.Card!.DraftId);

        var outcome = await f.Handler.HandleAsync(draft.DraftId, f.Sign(draft, ApprovalDecisionKind.ApproveOnce), TestContext.Current.CancellationToken);

        Assert.Equal(DraftDecisionStatus.Ok, outcome.Status);
        Assert.Equal(draft.Post.Body, f.Adapter.Posts.Single().Post.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_blank_body(string text)
    {
        using var f = new DraftFixture();
        var before = f.Store.All.Count;

        var result = await Creator(f).CreateAsync(Proposal(text: text), TestContext.Current.CancellationToken);

        Assert.False(result.Created);
        Assert.Equal("bad_body", result.Code);
        Assert.Equal(before, f.Store.All.Count);
    }

    [Fact]
    public async Task Rejects_an_overlong_body_a_read_action_and_a_profile_of_another_agent()
    {
        using var f = new DraftFixture();
        var creator = Creator(f, new CommunityDraftOptions(MaxEditedBodyChars: 10));

        Assert.Equal("bad_body", (await creator.CreateAsync(Proposal(), TestContext.Current.CancellationToken)).Code);
        Assert.Equal("unsupported_action", (await Creator(f).CreateAsync(Proposal(action: CommunityAction.Read), TestContext.Current.CancellationToken)).Code);
        var other = Proposal() with { AgentId = "agent-2" };
        Assert.Equal("bad_profile", (await Creator(f).CreateAsync(other, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public async Task Each_call_makes_a_new_draft_id_and_approval()
    {
        using var f = new DraftFixture();
        var creator = Creator(f);

        var a = await creator.CreateAsync(Proposal(), TestContext.Current.CancellationToken);
        var b = await creator.CreateAsync(Proposal(), TestContext.Current.CancellationToken);

        Assert.NotEqual(a.Card!.DraftId, b.Card!.DraftId);
        Assert.NotEqual(a.Card.Approval.ApprovalId, b.Card.Approval.ApprovalId);
    }
}
