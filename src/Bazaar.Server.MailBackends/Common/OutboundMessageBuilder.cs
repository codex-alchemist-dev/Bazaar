using System.Security.Cryptography;
using System.Text;
using MimeKit;
using Bazaar.Server.Mail;

namespace Bazaar.Server.MailBackends.Common;

/// <summary>
/// Builds the MIME message for one outbound mail. Pure: no network, so every back end shares the same output.
/// The From header comes from the server's mailbox, never from the model (doc 22, outbound step 3).
/// The AI-agent footer is required and cannot be left empty (doc 22, outbound step 3; setting mail.footer.wording).
/// </summary>
public static class OutboundMessageBuilder
{
    /// <summary>Builds a message whose Message-ID is derived from the idempotency key, so a retry carries the same identity.</summary>
    public static MimeMessage Build(OutboundMail mail, string footer)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (string.IsNullOrWhiteSpace(mail.Key)) throw new ArgumentException("Idempotency key is required.", nameof(mail));
        if (string.IsNullOrWhiteSpace(footer)) throw new ArgumentException("The AI-agent footer cannot be empty.", nameof(footer));
        if (mail.To.Count == 0) throw new ArgumentException("At least one recipient is required.", nameof(mail));
        if (mail.Subject.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("Subject cannot contain line breaks.", nameof(mail));

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(mail.From.Address));
        foreach (var to in mail.To)
        {
            message.To.Add(MailboxAddress.Parse(to));
        }

        message.Subject = mail.Subject;
        message.MessageId = MessageIdFor(mail.Key, mail.From.Domain);
        message.Body = new TextPart("plain")
        {
            Text = mail.TextBody.TrimEnd() + "\n\n-- \n" + footer.Trim() + "\n",
        };
        return message;
    }

    /// <summary>Deterministic Message-ID without angle brackets (MimeKit adds them on write): a hash keeps the raw key out of headers.</summary>
    public static string MessageIdFor(string key, string domain)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"{Convert.ToHexString(digest).ToLowerInvariant()}@{domain}";
    }
}
