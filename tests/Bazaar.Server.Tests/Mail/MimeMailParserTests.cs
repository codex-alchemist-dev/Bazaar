using System.Globalization;
using System.Text;
using Bazaar.Server.Mail.Parsing;

namespace Bazaar.Server.Tests.Mail;

public sealed class MimeMailParserTests
{
    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text.Replace("\n", "\r\n"));

    private const string _plainMessage = """
        From: Sender <sender@example.test>
        To: agent@example.test
        Subject: Hello
        Message-ID: <one@example.test>
        Authentication-Results: example.test; spf=pass; dkim=pass
        Content-Type: text/plain

        Body text.
        """;

    [Fact]
    public void Parses_plain_message_headers_and_body()
    {
        var parsed = new MimeMailParser().Parse(Bytes(_plainMessage));

        Assert.Equal("one@example.test", parsed.MessageId);
        Assert.Equal("sender@example.test", parsed.FromAddress);
        Assert.Equal("Hello", parsed.Subject);
        Assert.Contains("Body text.", parsed.TextBody);
        Assert.Empty(parsed.Attachments);
        Assert.False(parsed.AutoSubmitted);
        Assert.Contains("Authentication-Results", parsed.AuthenticationResults.Keys);
    }

    [Fact]
    public void Flags_auto_submitted_mail_and_ignores_auto_submitted_no()
    {
        var auto = Bytes("From: a@example.test\nAuto-Submitted: auto-replied\nContent-Type: text/plain\n\nx");
        var normal = Bytes("From: a@example.test\nAuto-Submitted: no\nContent-Type: text/plain\n\nx");

        Assert.True(new MimeMailParser().Parse(auto).AutoSubmitted);
        Assert.False(new MimeMailParser().Parse(normal).AutoSubmitted);
    }

    [Fact]
    public void Rejects_message_over_byte_limit_before_parsing()
    {
        var limits = new MailLimits { MaxMessageBytes = 20 };

        var ex = Assert.Throws<MailLimitException>(() => new MimeMailParser(limits).Parse(Bytes(_plainMessage)));

        Assert.Equal("bytes", ex.Limit);
    }

    [Fact]
    public void Rejects_too_many_attachments()
    {
        var parts = string.Concat(Enumerable.Range(0, 3).Select(i =>
            $"\n--b\nContent-Type: application/pdf; name=\"f{i}.pdf\"\nContent-Disposition: attachment; filename=\"f{i}.pdf\"\nContent-Transfer-Encoding: base64\n\nQUJD\n"));
        var raw = Bytes($"From: a@example.test\nContent-Type: multipart/mixed; boundary=b\n\n--b\nContent-Type: text/plain\n\nhi{parts}\n--b--\n");

        var ex = Assert.Throws<MailLimitException>(() => new MimeMailParser(new MailLimits { MaxAttachments = 2 }).Parse(raw));

        Assert.Equal("attachments", ex.Limit);
    }

    [Fact]
    public void Counts_attachments_within_limit()
    {
        var raw = Bytes("From: a@example.test\nContent-Type: multipart/mixed; boundary=b\n\n--b\nContent-Type: text/plain\n\nhi\n--b\nContent-Type: application/pdf; name=\"f.pdf\"\nContent-Disposition: attachment; filename=\"f.pdf\"\nContent-Transfer-Encoding: base64\n\nQUJD\n--b--\n");

        var parsed = new MimeMailParser().Parse(raw);

        Assert.Single(parsed.Attachments);
        Assert.Equal("f.pdf", parsed.Attachments[0].FileName);
        Assert.Equal("application/pdf", parsed.Attachments[0].MediaType);
    }

    [Fact]
    public void Rejects_nesting_deeper_than_limit()
    {
        var sb = new StringBuilder("From: a@example.test\nContent-Type: multipart/mixed; boundary=b0\n\n");
        const int levels = 12;
        for (var i = 0; i < levels; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"--b{i}\nContent-Type: multipart/mixed; boundary=b{i + 1}\n\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"--b{levels}\nContent-Type: text/plain\n\nx\n--b{levels}--\n");
        for (var i = levels - 1; i >= 0; i--) sb.Append(CultureInfo.InvariantCulture, $"--b{i}--\n");

        var ex = Assert.Throws<MailLimitException>(() => new MimeMailParser(new MailLimits { MaxDepth = 4 }).Parse(Bytes(sb.ToString())));

        Assert.Equal("depth", ex.Limit);
    }

    [Fact]
    public void Limits_reject_non_positive_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MimeMailParser(new MailLimits { MaxParts = 0 }).Parse(Bytes(_plainMessage)));
    }
}
