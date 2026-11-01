using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.Common;

namespace Bazaar.Server.MailBackends.Tests.Common;

public sealed class OutboundMessageBuilderTests
{
    private static OutboundMail Sample(string key = "k-1") =>
        new(key, new MailboxRef("example.org", "ada"), ["owner@example.net"], "Hello", "Body text");

    [Fact]
    public void From_comes_from_the_server_mailbox_and_footer_is_appended()
    {
        var message = OutboundMessageBuilder.Build(Sample(), "This mail was sent by an AI agent.");
        Assert.Equal("ada@example.org", message.From.Mailboxes.Single().Address);
        var body = Assert.IsType<MimeKit.TextPart>(message.Body).Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("Body text", body, StringComparison.Ordinal);
        Assert.EndsWith("This mail was sent by an AI agent.\n", body);
    }

    [Fact]
    public void Message_id_is_stable_for_one_key_and_hides_the_raw_key()
    {
        var first = OutboundMessageBuilder.Build(Sample("k-1"), "footer");
        var second = OutboundMessageBuilder.Build(Sample("k-1"), "footer");
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.DoesNotContain("k-1", first.MessageId);
        Assert.EndsWith("@example.org", first.MessageId, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_footer_is_refused()
    {
        Assert.Throws<ArgumentException>(() => OutboundMessageBuilder.Build(Sample(), "  "));
    }

    [Fact]
    public void Line_breaks_in_subject_are_refused()
    {
        var bad = Sample() with { Subject = "hi\r\nBcc: x@example.net" };
        Assert.Throws<ArgumentException>(() => OutboundMessageBuilder.Build(bad, "footer"));
    }

    [Fact]
    public void Repeated_key_returns_the_first_receipt()
    {
        var ledger = new SendLedger();
        Assert.Null(ledger.Find("k-1"));
        ledger.Record(new SendReceipt("k-1", "<id@example.org>", AlreadySent: false));
        var again = ledger.Find("k-1");
        Assert.NotNull(again);
        Assert.True(again!.AlreadySent);
        Assert.Equal("<id@example.org>", again.BackendMessageId);
    }
}
