using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Mail.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Tests;

public class MailPanesTests
{
    [Fact]
    public void Modules_register_the_four_mail_kinds_in_order()
    {
        var ids = MailPanes.Modules.Select(m => m.Id).ToArray();

        Assert.Equal(new[] { PaneKinds.MailQuarantine, PaneKinds.MailMailbox, PaneKinds.MailApproval, PaneKinds.MailDns }, ids);
        Assert.Equal(ids, MailFixtures.Registry().Kinds);
    }

    [Fact]
    public void Each_mail_kind_is_built_as_its_own_view_model()
    {
        var registry = MailFixtures.Registry();

        Assert.IsType<MailQuarantinePaneViewModel>(registry.Create(MailFixtures.Descriptor(PaneKinds.MailQuarantine, MailFixtures.QuarantineModel)));
        Assert.IsType<MailMailboxPaneViewModel>(registry.Create(MailFixtures.Descriptor(PaneKinds.MailMailbox, MailFixtures.MailboxModel)));
        Assert.IsType<MailApprovalPaneViewModel>(registry.Create(MailFixtures.Descriptor(PaneKinds.MailApproval, MailFixtures.ApprovalModel)));
        Assert.IsType<MailDnsPaneViewModel>(registry.Create(MailFixtures.Descriptor(PaneKinds.MailDns, MailFixtures.DnsModel)));
    }

    [Theory]
    [InlineData(PaneKinds.MailQuarantine, """{"digest":{"date":"2026-11-17","held":0,"released":0,"purged":0,"reported":0,"topSenderDomains":[]}}""")]
    [InlineData(PaneKinds.MailQuarantine, """{"digest":{"date":"2026-11-17","held":0,"released":0,"purged":0,"reported":0,"topSenderDomains":[]},"items":[{"senderDomain":"a.example","reasonCodes":[],"receivedAt":"2026-11-17T09:30:00+00:00","state":"held"}]}""")]
    [InlineData(PaneKinds.MailQuarantine, """{"digest":{"date":"2026-11-17","held":0,"released":0,"purged":0,"reported":0,"topSenderDomains":[]},"items":[{"senderDomain":"a.example","reasonCodes":[],"authResult":"none","receivedAt":"2026-11-17T09:30:00+00:00","state":"lost"}]}""")]
    [InlineData(PaneKinds.MailQuarantine, """{"digest":{"held":0,"released":0,"purged":0,"reported":0,"topSenderDomains":[]},"items":[]}""")]
    [InlineData(PaneKinds.MailQuarantine, """{"digest":{"date":"17-11-2026","held":0,"released":0,"purged":0,"reported":0,"topSenderDomains":[]},"items":[]}""")]
    [InlineData(PaneKinds.MailQuarantine, """{"digest":{"date":"2026-11-17T09:30:00+00:00","held":0,"released":0,"purged":0,"reported":0,"topSenderDomains":[]},"items":[]}""")]
    [InlineData(PaneKinds.MailApproval, """{"from":"a","recipients":["b"],"subject":"b","body":"c","footerPreview":"d","reason":"e"}""")]
    [InlineData(PaneKinds.MailMailbox, """{"messages":[{"from":"a","subject":"b","receivedAt":"2026-11-17T09:30:00+00:00","links":[]}]}""")]
    [InlineData(PaneKinds.MailMailbox, """{"messages":[{"from":"a","subject":"b","receivedAt":"not a date","text":"c","links":[]}]}""")]
    [InlineData(PaneKinds.MailApproval, """{"approvalId":"appr-1","from":"a","recipients":[],"subject":"b","body":"c","footerPreview":"d","reason":"e"}""")]
    [InlineData(PaneKinds.MailApproval, """{"approvalId":"appr-1","from":"a","recipients":["b"],"subject":"b","body":"c","footerPreview":"d"}""")]
    [InlineData(PaneKinds.MailDns, """{"domain":"example.com","rows":[{"kind":"Spf","status":"Maybe","detail":"x","fix":null}]}""")]
    [InlineData(PaneKinds.MailDns, """{"domain":"example.com","rows":[{"kind":"Mx","status":"Pass","detail":"x","fix":null}]}""")]
    public void A_missing_or_bad_required_field_falls_back_to_the_generic_view(string kind, string json)
    {
        var registry = MailFixtures.Registry();

        Assert.IsType<GenericPaneViewModel>(registry.Create(MailFixtures.Descriptor(kind, json)));
    }

    [Fact]
    public void A_missing_required_field_raises_PaneModelException_in_the_view_model()
    {
        var descriptor = MailFixtures.Descriptor(PaneKinds.MailQuarantine, """{"digest":{"date":"2026-11-17","held":1,"released":0,"purged":0,"reported":0,"topSenderDomains":[]},"items":[{"senderDomain":"a.example","reasonCodes":[],"receivedAt":"2026-11-17T09:30:00+00:00","state":"held"}]}""");

        Assert.Throws<PaneModelException>(() => new MailQuarantinePaneViewModel(descriptor));
    }

    [Fact]
    public void Every_mail_pane_keeps_its_server_kind_and_has_a_title()
    {
        var registry = MailFixtures.Registry();
        var panes = new[]
        {
            registry.Create(MailFixtures.Descriptor(PaneKinds.MailQuarantine, MailFixtures.QuarantineModel)),
            registry.Create(MailFixtures.Descriptor(PaneKinds.MailMailbox, MailFixtures.MailboxModel)),
            registry.Create(MailFixtures.Descriptor(PaneKinds.MailApproval, MailFixtures.ApprovalModel)),
            registry.Create(MailFixtures.Descriptor(PaneKinds.MailDns, MailFixtures.DnsModel)),
        };

        var titles = new[] { "Mail · Quarantine", "Mail · Mailbox", "Mail · Approval", "Mail · DNS" };
        Assert.Equal(titles, panes.Select(p => p.Title));
        Assert.Equal(MailPanes.Modules.Select(m => m.Id), panes.Select(p => p.Kind));
    }
}
