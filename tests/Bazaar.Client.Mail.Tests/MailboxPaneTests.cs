using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Mail.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Tests;

public class MailboxPaneTests
{
    [Fact]
    public void Reads_messages_as_plain_text_and_keeps_links_as_text()
    {
        var pane = new MailMailboxPaneViewModel(MailFixtures.Descriptor(PaneKinds.MailMailbox, MailFixtures.MailboxModel));

        var message = Assert.Single(pane.Messages);
        Assert.Equal("Sam <sam@partner.example.org>", message.From);
        Assert.Equal("Invoice <b>now</b>", message.Subject);
        Assert.Equal("Hello <img src=\"https://tracker.example/p.gif\"> world", message.Text);
        Assert.Equal(3, message.Links.Count);
    }

    [Theory]
    [InlineData("https://docs.partner.example.org/invoice?id=7", "docs.partner.example.org")]
    [InlineData("http://user@evil.example:8080/login", "evil.example")]
    [InlineData("javascript:alert(1)", "no host")]
    [InlineData("not a url", "no host")]
    public void A_link_is_shown_by_its_real_host(string url, string host)
    {
        Assert.Equal(host, MailFormat.LinkHost(url));
    }

    [AvaloniaFact]
    public void Mailbox_view_shows_plain_text_hosts_and_no_markup_or_remote_content()
    {
        var view = MailFixtures.Registry().CreateView(
            new MailMailboxPaneViewModel(MailFixtures.Descriptor(PaneKinds.MailMailbox, MailFixtures.MailboxModel)));
        var texts = MailFixtures.Texts((Control)view);

        // Markup in the text is shown as typed, not rendered; the html field is never read.
        Assert.Contains("Hello <img src=\"https://tracker.example/p.gif\"> world", texts);
        Assert.Contains("Subject: Invoice <b>now</b>", texts);
        Assert.DoesNotContain(texts, t => t.Contains("HTML version", StringComparison.Ordinal));

        // Every link shows its real host and is not opened.
        Assert.Contains("Link to docs.partner.example.org (not opened)", texts);
        Assert.Contains("Link to evil.example (not opened)", texts);
        Assert.Contains("Link to no host (not opened)", texts);
    }

    [AvaloniaFact]
    public void Mailbox_view_has_no_link_button_image_or_web_control_and_is_named()
    {
        var view = MailFixtures.Registry().CreateView(
            new MailMailboxPaneViewModel(MailFixtures.Descriptor(PaneKinds.MailMailbox, MailFixtures.MailboxModel)));
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Empty(window.GetVisualDescendants().OfType<Button>());
        Assert.Empty(window.GetVisualDescendants().OfType<Image>());
        Assert.True(MailFixtures.HasAutomationName((Control)view, "Mail mailbox"));
        Assert.True(MailFixtures.HasAutomationName((Control)view, "Mail from Sam <sam@partner.example.org>"));
        Assert.Empty(AccessibleControlScan.Find(window));
        window.Close();
    }
}
