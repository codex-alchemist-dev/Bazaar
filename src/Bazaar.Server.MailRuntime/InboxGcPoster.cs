using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.MailInbound;

namespace Bazaar.Server.MailRuntime;

/// <summary>
/// The inbound pipeline's only path into a GC log, over <see cref="IMailGcAppender"/>. Every post is idempotent: the client message id is derived from the
/// mail message id, so a repeat returns the first event. Payloads carry ids, a sender domain, times and closed codes. No subject, display name,
/// address or body field exists here, so none can be posted.
/// </summary>
public sealed class InboxGcPoster(IMailGcAppender appender) : IInboxGcPoster
{
    /// <summary>Author id for the mail bridge (<c>external_bridge</c>): the ASCII bytes of "mail" lead the value so it is recognisable in the log.</summary>
    public static readonly Guid BridgeAuthorId = new("6d61696c-0000-4000-8000-000000000001");

    /// <summary>Author id for server-generated mail notices, the same system id the GC editor uses.</summary>
    public static readonly Guid SystemAuthorId = Guid.Empty;

    public async Task<Guid> PostExternalMailAsync(ExternalMailEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var payload = Json(w =>
        {
            w.WriteString("mail_message_id", entry.MailMessageId);
            w.WriteString("mailbox_id", entry.MailboxId);
            w.WriteString("sender_domain", entry.SenderDomain);
            w.WriteString("received_at", entry.ReceivedAt);
            WriteCodes(w, entry.ScreenCodes, "screen_codes");
        });
        var result = await appender.AppendAsync(new AppendRequest(
            entry.InboxGcId, GcEventKinds.ExternalMail, AuthorKind.ExternalBridge, BridgeAuthorId, SourceTrust.External, "mail.ext:" + entry.MailMessageId.ToString("N"))
        {
            // Hard taint on external mail is decided here, not left to a hook default (doc 15).
            TaintedAtWrite = true,
            PayloadJson = payload,
        }, ct).ConfigureAwait(false);
        return result.EventId;
    }

    public async Task PostQuarantineNoticeAsync(QuarantineNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var payload = Json(w =>
        {
            w.WriteString("notice", QuarantineNotice.Kind);
            w.WriteString("mail_message_id", notice.MailMessageId);
            w.WriteNumber("held_count", notice.HeldCount);
            WriteCodes(w, notice.ReasonCodes, "reason_codes");
        });
        await appender.AppendAsync(new AppendRequest(
            notice.InboxGcId, GcEventKinds.System, AuthorKind.System, SystemAuthorId, SourceTrust.System, "mail.qn:" + notice.MailMessageId.ToString("N"))
        {
            PayloadJson = payload,
        }, ct).ConfigureAwait(false);
    }

    public async Task PostTriageHandoverAsync(Guid inboxGcId, MailTriageHandover handover, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handover);
        // The hand-over is closed vocabulary only; an invalid one is refused rather than posted.
        if (!handover.IsValid())
        {
            throw new ArgumentException("The mail_triage hand-over is not valid.", nameof(handover));
        }

        var payload = Json(w =>
        {
            w.WriteString("schema", MailTriageHandover.SchemaId);
            w.WriteString("mail_message_id", handover.MailMessageId);
            w.WriteString("verdict", handover.Verdict switch
            {
                MailTriageVerdict.Routine => "routine",
                MailTriageVerdict.NeedsAttention => "needs_attention",
                _ => "escalate",
            });
            WriteCodes(w, handover.ReasonCodes, "reason_codes");
        });
        await appender.AppendAsync(new AppendRequest(
            inboxGcId, GcEventKinds.Handover, AuthorKind.System, SystemAuthorId, SourceTrust.System, "mail.tri:" + handover.MailMessageId.ToString("N"))
        {
            PayloadJson = payload,
        }, ct).ConfigureAwait(false);
    }

    private static void WriteCodes(Utf8JsonWriter w, IReadOnlyList<string> codes, string name)
    {
        w.WriteStartArray(name);
        foreach (var code in codes)
        {
            w.WriteStringValue(code);
        }

        w.WriteEndArray();
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
