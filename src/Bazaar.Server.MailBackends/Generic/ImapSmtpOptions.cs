using MailKit.Security;

namespace Bazaar.Server.MailBackends.Generic;

/// <summary>Endpoints for one operator-named IMAP and SMTP provider. Holds no credentials (see <see cref="IMailCredentialSource"/>).</summary>
public sealed record ImapSmtpOptions(
    string ImapHost,
    int ImapPort,
    SecureSocketOptions ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    SecureSocketOptions SmtpSecurity)
{
    /// <summary>Rejects plain-text transports; the only exception is an explicit opt-in on loopback test servers.</summary>
    public void Validate(bool allowPlaintextLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(ImapHost)) throw new InvalidOperationException("IMAP host is required.");
        if (string.IsNullOrWhiteSpace(SmtpHost)) throw new InvalidOperationException("SMTP host is required.");
        if (ImapPort is <= 0 or > 65535 || SmtpPort is <= 0 or > 65535) throw new InvalidOperationException("Ports must be 1 to 65535.");
        if (!allowPlaintextLoopback && (ImapSecurity == SecureSocketOptions.None || SmtpSecurity == SecureSocketOptions.None))
        {
            throw new InvalidOperationException("Mail transports must use TLS (StartTLS or implicit TLS).");
        }
    }
}

/// <summary>A login for one provider account. Resolved at call time from the server's secret store; never cached in options.</summary>
public sealed record MailCredential(string UserName, string Password);

/// <summary>Resolves the login for a mailbox. Implemented over the Identity Service's ISecretStore (doc 14).</summary>
public interface IMailCredentialSource
{
    Task<MailCredential> GetAsync(Bazaar.Server.Mail.MailboxRef box, CancellationToken ct);
}
