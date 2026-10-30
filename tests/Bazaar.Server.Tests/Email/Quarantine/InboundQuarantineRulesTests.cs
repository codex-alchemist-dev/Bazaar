using System.Collections.Immutable;
using Bazaar.Server.Email.Quarantine;

namespace Bazaar.Server.Tests.Email.Quarantine;

public class InboundQuarantineRulesTests
{
    private static readonly InboundQuarantineOptions _defaults = new();

    private static InboundHeaders Clean() => new(
        From: "Alice Smith <alice@example.org>",
        SpfResult: "pass",
        DkimResult: "pass",
        DmarcResult: "pass",
        MessageBytes: 2048,
        Attachments: [],
        Links: []);

    private static InboundHeaders WithAttachments(params string[] fileNames) =>
        Clean() with { Attachments = fileNames.Select(name => new AttachmentInfo(name, null)).ToList() };

    [Fact]
    public void Clean_message_is_delivered_with_no_reasons()
    {
        var decision = InboundQuarantineRules.Evaluate(Clean(), _defaults);

        Assert.Equal(QuarantineAction.Deliver, decision.Action);
        Assert.Empty(decision.Reasons);
    }

    [Theory]
    [InlineData("SPF fails", "fail", "pass", "pass")]
    [InlineData("DKIM fails", "pass", "fail", "pass")]
    [InlineData("DMARC fails", "pass", "pass", "fail")]
    [InlineData("DMARC fails in capitals", "pass", "pass", "FAIL")]
    public void Any_failed_authentication_result_is_quarantined(string _, string spf, string dkim, string dmarc)
    {
        var headers = Clean() with { SpfResult = spf, DkimResult = dkim, DmarcResult = dmarc };

        var decision = InboundQuarantineRules.Evaluate(headers, _defaults);

        Assert.Equal(QuarantineAction.Quarantine, decision.Action);
        Assert.Equal(new[] { QuarantineReason.AuthenticationFailed }, decision.Reasons.ToArray());
    }

    [Fact]
    public void Results_that_are_not_fail_do_not_quarantine()
    {
        var headers = Clean() with { SpfResult = "none", DkimResult = null, DmarcResult = "neutral" };

        Assert.Equal(QuarantineAction.Deliver, InboundQuarantineRules.Evaluate(headers, _defaults).Action);
    }

    [Fact]
    public void Display_name_impersonating_a_different_address_is_quarantined()
    {
        var headers = Clean() with { From = "billing@bank.example <attacker@evil.example>" };

        var decision = InboundQuarantineRules.Evaluate(headers, _defaults);

        Assert.Equal(QuarantineAction.Quarantine, decision.Action);
        Assert.Equal(new[] { QuarantineReason.DisplayNameMismatch }, decision.Reasons.ToArray());
    }

    [Fact]
    public void Display_name_with_the_same_address_is_delivered()
    {
        var headers = Clean() with { From = "alice@example.org <Alice@EXAMPLE.org>" };

        Assert.Equal(QuarantineAction.Deliver, InboundQuarantineRules.Evaluate(headers, _defaults).Action);
    }

    [Theory]
    [InlineData("invoice.exe", QuarantineReason.ExecutableAttachment)]
    [InlineData("report.PDF.scr", QuarantineReason.ExecutableAttachment)]
    [InlineData("setup.ps1", QuarantineReason.ExecutableAttachment)]
    [InlineData("payload.exe.", QuarantineReason.ExecutableAttachment)]
    [InlineData("budget.xlsm", QuarantineReason.MacroAttachment)]
    [InlineData("letter.DOCM", QuarantineReason.MacroAttachment)]
    public void Risky_attachment_types_are_quarantined(string fileName, QuarantineReason expected)
    {
        var decision = InboundQuarantineRules.Evaluate(WithAttachments(fileName), _defaults);

        Assert.Equal(QuarantineAction.Quarantine, decision.Action);
        Assert.Equal(new[] { expected }, decision.Reasons.ToArray());
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    public void Other_attachment_types_are_delivered(string fileName)
    {
        Assert.Equal(QuarantineAction.Deliver, InboundQuarantineRules.Evaluate(WithAttachments(fileName), _defaults).Action);
    }

    [Fact]
    public void Link_whose_text_names_another_host_is_quarantined()
    {
        var headers = Clean() with
        {
            Links = [new LinkInfo("www.bank.example", "https://phish.example/login")],
        };

        var decision = InboundQuarantineRules.Evaluate(headers, _defaults);

        Assert.Equal(new[] { QuarantineReason.LinkTextMismatch }, decision.Reasons.ToArray());
    }

    [Fact]
    public void Link_whose_text_matches_its_host_is_delivered()
    {
        var headers = Clean() with
        {
            Links =
            [
                new LinkInfo("www.bank.example", "https://WWW.bank.example/account"),
                new LinkInfo("click here", "https://phish.example/login"),
            ],
        };

        Assert.Equal(QuarantineAction.Deliver, InboundQuarantineRules.Evaluate(headers, _defaults).Action);
    }

    [Fact]
    public void Default_size_threshold_is_10_MiB()
    {
        Assert.Equal(10_485_760L, _defaults.MaxMessageBytes);

        var atLimit = Clean() with { MessageBytes = 10_485_760L };
        var overLimit = Clean() with { MessageBytes = 10_485_761L };

        Assert.Equal(QuarantineAction.Deliver, InboundQuarantineRules.Evaluate(atLimit, _defaults).Action);

        var decision = InboundQuarantineRules.Evaluate(overLimit, _defaults);
        Assert.Equal(new[] { QuarantineReason.OversizeMessage }, decision.Reasons.ToArray());
    }

    [Fact]
    public void Default_attachment_threshold_is_five()
    {
        Assert.Equal(5, _defaults.MaxAttachments);

        var five = WithAttachments("a.txt", "b.txt", "c.txt", "d.txt", "e.txt");
        var six = WithAttachments("a.txt", "b.txt", "c.txt", "d.txt", "e.txt", "f.txt");

        Assert.Equal(QuarantineAction.Deliver, InboundQuarantineRules.Evaluate(five, _defaults).Action);

        var decision = InboundQuarantineRules.Evaluate(six, _defaults);
        Assert.Equal(new[] { QuarantineReason.TooManyAttachments }, decision.Reasons.ToArray());
    }

    [Fact]
    public void Custom_thresholds_are_used_instead_of_defaults()
    {
        var options = new InboundQuarantineOptions { MaxMessageBytes = 100, MaxAttachments = 0 };

        var decision = InboundQuarantineRules.Evaluate(WithAttachments("a.txt") with { MessageBytes = 101 }, options);

        Assert.Equal(new[] { QuarantineReason.OversizeMessage, QuarantineReason.TooManyAttachments }, decision.Reasons.ToArray());
    }

    [Fact]
    public void Reasons_are_returned_distinct_and_in_enum_order()
    {
        // Built in a deliberately different order from the enum; the decision must still sort them.
        var headers = new InboundHeaders(
            From: "billing@bank.example <attacker@evil.example>",
            SpfResult: "fail",
            DkimResult: null,
            DmarcResult: null,
            MessageBytes: 20_000_000,
            Attachments:
            [
                new AttachmentInfo("macro.xlsm", null),
                new AttachmentInfo("run.exe", null),
                new AttachmentInfo("more.txt", null),
                new AttachmentInfo("more2.txt", null),
                new AttachmentInfo("more3.txt", null),
                new AttachmentInfo("more4.txt", null),
            ],
            Links: [new LinkInfo("www.bank.example", "https://phish.example")]);

        var decision = InboundQuarantineRules.Evaluate(headers, _defaults);

        Assert.Equal(
            ImmutableArray.Create(
                QuarantineReason.AuthenticationFailed,
                QuarantineReason.DisplayNameMismatch,
                QuarantineReason.ExecutableAttachment,
                QuarantineReason.MacroAttachment,
                QuarantineReason.LinkTextMismatch,
                QuarantineReason.OversizeMessage,
                QuarantineReason.TooManyAttachments),
            decision.Reasons);
    }

    [Fact]
    public void Negative_message_size_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InboundQuarantineRules.Evaluate(Clean() with { MessageBytes = -1 }, _defaults));
    }

    [Fact]
    public void Non_positive_size_threshold_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InboundQuarantineRules.Evaluate(Clean(), new InboundQuarantineOptions { MaxMessageBytes = 0 }));
    }
}
