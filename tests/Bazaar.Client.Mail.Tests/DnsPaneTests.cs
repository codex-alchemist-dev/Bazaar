using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Mail.Abstractions;
using Bazaar.Client.Mail.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Tests;

public class DnsPaneTests
{
    [Fact]
    public void Reads_each_report_row_with_its_kind_status_and_fix()
    {
        var pane = new MailDnsPaneViewModel(MailFixtures.Descriptor(PaneKinds.MailDns, MailFixtures.DnsModel));

        Assert.Equal("example.com", pane.Domain);
        var expected = new[]
        {
            new MailDnsRow(MailDnsCheckKind.Spf, MailDnsStatus.Warn, "SPF ends in ~all.", "End the record with -all."),
            new MailDnsRow(MailDnsCheckKind.Dkim, MailDnsStatus.Pass, "The DKIM key is present.", null),
            new MailDnsRow(MailDnsCheckKind.Dmarc, MailDnsStatus.Fail, "No DMARC record was found.", "Publish a DMARC record with p=none."),
            new MailDnsRow(MailDnsCheckKind.Ptr, MailDnsStatus.NotApplicable, "The back end is hosted.", null),
            new MailDnsRow(MailDnsCheckKind.BulkSender, MailDnsStatus.Warn, "Expected volume is over 5,000 a day.", "Plan for bulk-sender rules."),
        };
        Assert.Equal(expected, pane.Rows);
    }

    [Theory]
    [InlineData(MailDnsCheckKind.Spf, "SPF")]
    [InlineData(MailDnsCheckKind.Dkim, "DKIM")]
    [InlineData(MailDnsCheckKind.Dmarc, "DMARC")]
    [InlineData(MailDnsCheckKind.Ptr, "PTR")]
    [InlineData(MailDnsCheckKind.BulkSender, "Bulk sender")]
    public void Each_check_has_a_label(MailDnsCheckKind kind, string label) =>
        Assert.Equal(label, MailFormat.DnsKind(kind));

    [Theory]
    [InlineData(MailDnsStatus.Pass, "Pass")]
    [InlineData(MailDnsStatus.Warn, "Warn")]
    [InlineData(MailDnsStatus.Fail, "Fail")]
    [InlineData(MailDnsStatus.NotApplicable, "N/A")]
    public void Each_status_has_chip_text(MailDnsStatus status, string text) =>
        Assert.Equal(text, MailFormat.DnsStatus(status));

    [AvaloniaFact]
    public void Dns_view_shows_a_status_chip_and_the_fix_hint_per_row()
    {
        var view = MailFixtures.Registry().CreateView(
            new MailDnsPaneViewModel(MailFixtures.Descriptor(PaneKinds.MailDns, MailFixtures.DnsModel)));
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();

        var texts = MailFixtures.Texts((Control)view);
        Assert.Contains("Domain: example.com", texts);
        Assert.Contains("Fix: End the record with -all.", texts);
        Assert.Contains("Fix: Publish a DMARC record with p=none.", texts);
        Assert.Contains("Fail", texts);
        Assert.Contains("N/A", texts);

        // The chip names its status, so the status is not conveyed by colour alone.
        var chip = window.GetVisualDescendants().OfType<Border>()
            .Single(b => AutomationProperties.GetName(b) == "DMARC status Fail");
        Assert.NotNull(chip);
        Assert.True(MailFixtures.HasAutomationName((Control)view, "Mail DNS checks"));
        Assert.Empty(AccessibleControlScan.Find(window));
        window.Close();
    }

    [Fact]
    public void A_row_with_no_fix_has_no_fix_text()
    {
        var pane = new MailDnsPaneViewModel(MailFixtures.Descriptor(PaneKinds.MailDns, """{"rows":[{"kind":"Dkim","status":"Pass","detail":"ok"}]}"""));

        Assert.Null(pane.Domain);
        Assert.Null(Assert.Single(pane.Rows).Fix);
    }
}
