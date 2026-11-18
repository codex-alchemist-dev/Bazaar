using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Mail.Abstractions;
using Bazaar.Client.Mail.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Tests;

public class QuarantinePaneTests
{
    [Fact]
    public void Reads_the_digest_and_the_metadata_rows()
    {
        var pane = new MailQuarantinePaneViewModel(MailFixtures.Descriptor(PaneKinds.MailQuarantine, MailFixtures.QuarantineModel));

        Assert.Equal(2, pane.Digest.Held);
        Assert.Equal(1, pane.Digest.Released);
        Assert.Equal(0, pane.Digest.Purged);
        Assert.Equal(1, pane.Digest.Reported);
        Assert.Equal(new[] { new MailSenderDomainCount("mail.example.net", 2) }, pane.Digest.TopSenderDomains);
        Assert.Equal(2, pane.Rows.Count);
        Assert.Equal(MailQuarantineState.Held, pane.Rows[0].State);
        Assert.Equal(MailQuarantineState.Released, pane.Rows[1].State);
        Assert.Equal("spf=fail dkim=none", pane.Rows[0].AuthResult);
        var reasons = new[] { "spf_fail", "new_sender" };
        Assert.Equal(reasons, pane.Rows[0].ReasonCodes);
    }

    [Fact]
    public void Formats_the_received_time_in_utc_and_names_the_reasons()
    {
        var pane = new MailQuarantinePaneViewModel(MailFixtures.Descriptor(PaneKinds.MailQuarantine, MailFixtures.QuarantineModel));

        Assert.Equal("2026-11-17 09:30 UTC", MailFormat.Received(pane.Rows[0].ReceivedAt));
        Assert.Equal("2026-11-17 03:05 UTC", MailFormat.Received(pane.Rows[1].ReceivedAt));
        Assert.Equal("spf_fail, new_sender", MailFormat.Reasons(pane.Rows[0].ReasonCodes));
        Assert.Equal("no reason codes", MailFormat.Reasons(pane.Rows[1].ReasonCodes));
    }

    [Fact]
    public void A_message_body_never_enters_the_model()
    {
        var pane = new MailQuarantinePaneViewModel(MailFixtures.Descriptor(PaneKinds.MailQuarantine, MailFixtures.QuarantineModel));

        var modelText = string.Join("|", pane.Rows.Select(r => r.ToString())) + pane.Digest;
        Assert.DoesNotContain("SECRET-BODY-TEXT", modelText);
    }

    [AvaloniaFact]
    public void Quarantine_view_never_shows_a_body()
    {
        var view = MailFixtures.Registry().CreateView(
            new MailQuarantinePaneViewModel(MailFixtures.Descriptor(PaneKinds.MailQuarantine, MailFixtures.QuarantineModel)));

        var texts = MailFixtures.Texts((Control)view);
        Assert.Contains("mail.example.net · held", texts);
        Assert.DoesNotContain(texts, t => t.Contains("SECRET-BODY-TEXT", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_state_is_rejected()
    {
        var descriptor = MailFixtures.Descriptor(PaneKinds.MailQuarantine, """{"digest":{"held":0,"released":0,"purged":0,"reported":0,"topSenderDomains":[]},"items":[{"senderDomain":"a.example","reasonCodes":[],"authResult":"none","receivedAt":"2026-11-17T09:30:00+00:00","state":"lost"}]}""");

        Assert.Throws<PaneModelException>(() => new MailQuarantinePaneViewModel(descriptor));
    }

    [AvaloniaFact]
    public void Quarantine_view_renders_with_automation_names_and_no_controls_to_act()
    {
        var view = MailFixtures.Registry().CreateView(
            new MailQuarantinePaneViewModel(MailFixtures.Descriptor(PaneKinds.MailQuarantine, MailFixtures.QuarantineModel)));
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(MailFixtures.HasAutomationName((Control)view, "Mail quarantine"));
        Assert.True(MailFixtures.HasAutomationName((Control)view, "Quarantined mail from mail.example.net, held"));
        Assert.Empty(AccessibleControlScan.Find(window));
        Assert.Empty(window.GetVisualDescendants().OfType<Button>());
        window.Close();
    }
}
