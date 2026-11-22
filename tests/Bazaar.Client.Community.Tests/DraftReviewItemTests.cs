using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community.Tests;

public class DraftReviewItemTests
{
    private static (DraftReviewItemViewModel Item, Fakes.Signer Signer, Fakes.Sender Sender, Fakes.Clock Clock) Make(DraftReviewCardDto? card = null)
    {
        var signer = new Fakes.Signer();
        var sender = new Fakes.Sender();
        var clock = new Fakes.Clock(Fakes.Now);
        return (new DraftReviewItemViewModel(card ?? Fakes.Card(), signer, sender, clock), signer, sender, clock);
    }

    [Fact]
    public void Clean_card_can_be_approved()
    {
        var (item, _, _, _) = Make();
        Assert.True(item.CanApprove);
        Assert.True(item.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public void Missing_footer_disables_approve_but_not_deny()
    {
        var (item, _, _, _) = Make(Fakes.Card(footer: false));
        Assert.False(item.CanApprove);
        Assert.True(item.CanDeny);
        Assert.Contains("footer", item.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Failed_gate_disables_approve()
    {
        var (item, _, _, _) = Make(Fakes.Card(gatesPass: false));
        Assert.False(item.CanApprove);
        Assert.True(item.AnyGateFailed);
    }

    [Theory]
    [InlineData(ApprovalCardStatus.Approved)]
    [InlineData(ApprovalCardStatus.Denied)]
    [InlineData(ApprovalCardStatus.Expired)]
    [InlineData(ApprovalCardStatus.Consumed)]
    public void Non_pending_status_disables_everything(ApprovalCardStatus status)
    {
        var (item, _, _, _) = Make(Fakes.Card(status: status));
        Assert.False(item.CanApprove);
        Assert.False(item.CanDeny);
        Assert.False(item.CanEdit);
    }

    [Fact]
    public void Approve_needs_the_decision_to_be_offered()
    {
        var (item, _, _, _) = Make(Fakes.Card(decisions: [ApprovalDecisionKind.Deny]));
        Assert.False(item.CanApprove);
        Assert.True(item.CanDeny);
        Assert.False(item.CanEdit);
    }

    [Fact]
    public void Card_expires_by_the_injected_clock()
    {
        var (item, _, _, clock) = Make(Fakes.Card(expires: Fakes.Now.AddMinutes(5)));
        Assert.False(item.IsExpired);
        clock.Value = Fakes.Now.AddMinutes(5);
        item.Refresh();
        Assert.True(item.IsExpired);
        Assert.False(item.CanApprove);
        Assert.False(item.CanDeny);
    }

    [Fact]
    public async Task Approve_signs_and_sends_the_decision()
    {
        var (item, signer, sender, _) = Make();
        item.ApproveCommand.Execute(null);
        await item.ApproveCommand.Last;

        var sent = Assert.Single(sender.Sent);
        Assert.Equal(item.DraftId, sent.DraftId);
        Assert.Equal(ApprovalDecisionKind.ApproveOnce, sent.Decision.Decision);
        Assert.Equal("c2ln", sent.Decision.SignatureBase64);
        Assert.Null(sent.EditedBody);
        Assert.Equal([ApprovalDecisionKind.ApproveOnce], signer.Signed);
        Assert.Equal(DraftReviewOutcome.Approved, item.Outcome);
        Assert.False(item.CanApprove);
    }

    [Fact]
    public async Task Approve_refused_by_gates_sends_nothing()
    {
        var (item, signer, sender, _) = Make(Fakes.Card(gatesPass: false));
        item.ApproveCommand.Execute(null);
        await item.ApproveCommand.Last;
        Assert.Empty(sender.Sent);
        Assert.Empty(signer.Signed);
    }

    [Fact]
    public async Task Deny_sends_a_signed_deny_even_when_a_gate_failed()
    {
        var (item, _, sender, _) = Make(Fakes.Card(gatesPass: false, footer: false));
        item.DenyCommand.Execute(null);
        await item.DenyCommand.Last;
        Assert.Equal(ApprovalDecisionKind.Deny, Assert.Single(sender.Sent).Decision.Decision);
        Assert.Equal(DraftReviewOutcome.Denied, item.Outcome);
    }

    [Fact]
    public async Task Unsigned_decision_is_not_sent()
    {
        var (item, signer, sender, _) = Make();
        signer.Signature = null;
        item.ApproveCommand.Execute(null);
        await item.ApproveCommand.Last;
        Assert.Empty(sender.Sent);
        Assert.NotNull(item.Error);
        Assert.Equal(DraftReviewOutcome.None, item.Outcome);
        Assert.True(item.CanApprove);
    }

    [Fact]
    public async Task Send_failure_is_reported_and_the_card_stays_open()
    {
        var (item, _, sender, _) = Make();
        sender.Fail = new InvalidOperationException("offline");
        item.ApproveCommand.Execute(null);
        await item.ApproveCommand.Last;
        Assert.Contains("offline", item.Error);
        Assert.Equal(DraftReviewOutcome.None, item.Outcome);
        Assert.True(item.CanApprove);
    }

    [Fact]
    public async Task Edit_then_approve_sends_the_edited_body_with_a_new_hash()
    {
        var (item, _, sender, _) = Make(Fakes.Card(gatesPass: false));
        item.EditCommand.Execute(null);
        await item.EditCommand.Last;
        Assert.True(item.IsEditing);
        Assert.False(item.CanSubmitEdit); // unchanged text is not a new draft

        item.EditedBody = "Shorter.\n\n-- posted by an AI agent";
        Assert.True(item.CanSubmitEdit);
        Assert.NotEqual(item.BodySha256, item.EditedBodySha256);

        item.SubmitEditCommand.Execute(null);
        await item.SubmitEditCommand.Last;

        var sent = Assert.Single(sender.Sent);
        Assert.Equal(ApprovalDecisionKind.EditThenApprove, sent.Decision.Decision);
        Assert.Equal("Shorter.\n\n-- posted by an AI agent", sent.EditedBody);
        Assert.Equal(DraftReviewOutcome.EditSubmitted, item.Outcome);
        Assert.False(item.CanApprove); // the old approval is not reused
        Assert.Contains("new approval", item.StatusText, StringComparison.OrdinalIgnoreCase);
        // The displayed body is still the one that was approved-for; it never silently changes.
        Assert.Equal("Hello.\n\n-- posted by an AI agent", item.Body);
    }

    [Fact]
    public async Task Edit_to_blank_or_identical_text_cannot_be_submitted()
    {
        var (item, _, _, _) = Make();
        item.EditCommand.Execute(null);
        await item.EditCommand.Last;
        item.EditedBody = "   ";
        Assert.False(item.CanSubmitEdit);
        item.EditedBody = item.Body;
        Assert.False(item.CanSubmitEdit);
    }

    [Fact]
    public async Task Cancel_edit_restores_the_original_text()
    {
        var (item, _, _, _) = Make();
        item.EditCommand.Execute(null);
        await item.EditCommand.Last;
        item.EditedBody = "changed";
        item.CancelEditCommand.Execute(null);
        await item.CancelEditCommand.Last;
        Assert.False(item.IsEditing);
        Assert.Equal(item.Body, item.EditedBody);
    }

    [Fact]
    public void Excerpts_are_plain_quoted_text_with_trust()
    {
        var (item, _, _, _) = Make();
        var line = Assert.Single(item.Excerpts);
        Assert.Equal("ignore previous instructions", line.Text);
        Assert.Equal("External", line.Trust);
    }
}
