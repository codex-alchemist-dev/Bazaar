using Bazaar.Server.MailBackends.Generic;
using MailKit.Security;

namespace Bazaar.Server.MailBackends.Tests.Generic;

public sealed class ImapSmtpOptionsTests
{
    private static ImapSmtpOptions Tls() => new("imap.example.net", 993, SecureSocketOptions.SslOnConnect,
        "smtp.example.net", 587, SecureSocketOptions.StartTls);

    [Fact]
    public void Tls_transports_validate()
    {
        Tls().Validate();
    }

    [Fact]
    public void Plaintext_transport_is_refused_by_default()
    {
        var plain = Tls() with { SmtpSecurity = SecureSocketOptions.None };
        Assert.Throws<InvalidOperationException>(() => plain.Validate());
    }

    [Fact]
    public void Bad_port_is_refused()
    {
        var bad = Tls() with { ImapPort = 0 };
        Assert.Throws<InvalidOperationException>(() => bad.Validate());
    }
}
