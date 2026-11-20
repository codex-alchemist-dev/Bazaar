using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Mail.Abstractions;
using Bazaar.Client.Mail.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Tests;

public class ApprovalPaneTests
{
    [Fact]
    public void Reads_the_server_set_card_fields()
    {
        var pane = new MailApprovalPaneViewModel(MailFixtures.Descriptor(PaneKinds.MailApproval, MailFixtures.ApprovalModel));

        Assert.Equal("appr-0001", pane.Card.ApprovalId);
        Assert.Equal("agent@bazaar.example", pane.Card.From);
        var recipients = new[] { "buyer@partner.example.org", "cc@partner.example.org" };
        Assert.Equal(recipients, pane.Card.Recipients);
        Assert.Equal("Renewal terms", pane.Card.Subject);
        Assert.Equal("Please see the attached <b>terms</b>.", pane.Card.Body);
        Assert.Equal("Sent by an AI agent for the operator. Contact: ops@example.org", pane.Card.FooterPreview);
        Assert.Equal("The body is built from inbound mail, so the send needs the owner.", pane.Card.Reason);
    }

    [Fact]
    public void A_card_with_no_recipient_is_rejected()
    {
        var descriptor = MailFixtures.Descriptor(PaneKinds.MailApproval, """{"approvalId":"appr-1","from":"a","recipients":[],"subject":"b","body":"c","footerPreview":"d","reason":"e"}""");

        Assert.Throws<PaneModelException>(() => new MailApprovalPaneViewModel(descriptor));
    }

    [Fact]
    public void A_card_without_an_approval_id_is_rejected()
    {
        var descriptor = MailFixtures.Descriptor(PaneKinds.MailApproval, """{"from":"a","recipients":["b"],"subject":"b","body":"c","footerPreview":"d","reason":"e"}""");

        Assert.Throws<PaneModelException>(() => new MailApprovalPaneViewModel(descriptor));
    }

    [AvaloniaFact]
    public void Approval_card_shows_the_fields_as_text_and_offers_no_action()
    {
        var view = MailFixtures.Registry().CreateView(
            new MailApprovalPaneViewModel(MailFixtures.Descriptor(PaneKinds.MailApproval, MailFixtures.ApprovalModel)));
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();

        var texts = MailFixtures.Texts((Control)view);
        Assert.Contains("Approval ID: appr-0001", texts);
        Assert.Contains("From (set by server): agent@bazaar.example", texts);
        Assert.Contains("To: buyer@partner.example.org, cc@partner.example.org", texts);
        Assert.Contains("Subject: Renewal terms", texts);
        Assert.Contains("Please see the attached <b>terms</b>.", texts);
        Assert.Contains("Footer: Sent by an AI agent for the operator. Contact: ops@example.org", texts);
        Assert.Contains("Why approval is needed: The body is built from inbound mail, so the send needs the owner.", texts);

        // Display only: no release, approve or send control.
        Assert.Empty(window.GetVisualDescendants().OfType<Button>());
        Assert.True(MailFixtures.HasAutomationName((Control)view, "Mail approval card"));
        Assert.Empty(AccessibleControlScan.Find(window));
        window.Close();
    }
}
