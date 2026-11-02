using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.Common;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Bazaar.Server.MailBackends.Generic;

/// <summary>
/// IMailBackend over generic IMAP (ingress) and SMTP (egress) through MailKit. Pull by default (doc 22, inbound step 1).
/// Raw bytes go out unparsed; screening happens in the mail core.
/// </summary>
/// <remarks>
/// Known gap: messages are marked Seen right after they are read, before the pipeline stores them. A crash in
/// that window loses the message. A follow-up adds an acknowledgement call so the mark happens after storage.
/// TODO(component): move under the Mail component once PR #125 merges.
/// </remarks>
public sealed class ImapSmtpBackend(
    string backendId,
    string domain,
    ImapSmtpOptions options,
    IMailCredentialSource credentials,
    SendLedger ledger,
    string footer,
    TimeProvider clock) : IMailBackend
{
    public async Task<IReadOnlyList<RawMail>> ReceiveAsync(MailboxRef box, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(options.ImapHost, options.ImapPort, options.ImapSecurity, ct);
        try
        {
            var login = await credentials.GetAsync(box, ct);
            await imap.AuthenticateAsync(login.UserName, login.Password, ct);

            var inbox = imap.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

            var query = SearchQuery.NotSeen.And(SearchQuery.ToContains(box.Address));
            var uids = await inbox.SearchAsync(query, ct);

            var received = new List<RawMail>(uids.Count);
            foreach (var uid in uids)
            {
                ct.ThrowIfCancellationRequested();
                var message = await inbox.GetMessageAsync(uid, ct);
                using var buffer = new MemoryStream();
                await message.WriteToAsync(buffer, ct);
                received.Add(new RawMail(
                    BackendId: $"{backendId}:{inbox.UidValidity}:{uid.Id}",
                    Mailbox: box,
                    Bytes: buffer.ToArray(),
                    ReceivedAt: clock.GetUtcNow()));
                await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
            }

            return received;
        }
        finally
        {
            await imap.DisconnectAsync(true, CancellationToken.None);
        }
    }

    public async Task<SendReceipt> SendAsync(OutboundMail mail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (ledger.Find(mail.Key) is { } prior) return prior;

        // The server sets From from the mailbox; a mailbox from another domain is refused here.
        if (!string.Equals(mail.From.Domain, domain, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sender domain does not match this back end.");
        }

        var message = OutboundMessageBuilder.Build(mail, footer);
        var messageId = OutboundMessageBuilder.MessageIdFor(mail.Key, domain);
        var login = await credentials.GetAsync(mail.From, ct);

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(options.SmtpHost, options.SmtpPort, options.SmtpSecurity, ct);
        try
        {
            await smtp.AuthenticateAsync(login.UserName, login.Password, ct);
            await smtp.SendAsync(message, ct);
        }
        finally
        {
            await smtp.DisconnectAsync(true, CancellationToken.None);
        }

        return ledger.Record(new SendReceipt(mail.Key, messageId, AlreadySent: false));
    }

    public async Task<BackendHealth> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(options.ImapHost, options.ImapPort, options.ImapSecurity, ct);
            await imap.DisconnectAsync(true, ct);

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(options.SmtpHost, options.SmtpPort, options.SmtpSecurity, ct);
            await smtp.DisconnectAsync(true, ct);
            return new BackendHealth(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the exception type goes out, never the message: hosts and logins can appear in it.
            return new BackendHealth(false, ex.GetType().Name);
        }
    }
}
