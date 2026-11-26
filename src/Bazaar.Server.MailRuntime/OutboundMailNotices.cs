using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.MailOutbound;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.MailRuntime;

/// <summary>
/// Owner notices for outbound mail, as a <c>system</c> GC event over <see cref="IMailGcAppender"/>. The target GC is
/// <see cref="MailRuntimeOptions.NoticesGcId"/> (PROPOSED <c>mail.notices.gc_id</c>); when it is not set the notice is skipped, counted and logged as a
/// warning. The payload holds ids, a reason code, the sender domain, a recipient count and a time: the notice type has no field for a subject, body or address.
/// </summary>
public sealed partial class OutboundMailNotices(IMailGcAppender appender, IMailRuntimeOptionsSource options, ILogger<OutboundMailNotices>? logger = null) : IOutboundMailNotices
{
    /// <summary>The <c>notice</c> value of the payload.</summary>
    public const string ApprovalRefusedKind = "mail_outbound_approval_refused";

    private long _skipped;

    /// <summary>How many notices were skipped because no target GC is configured.</summary>
    public long SkippedCount => Interlocked.Read(ref _skipped);

    public async Task PostApprovalRefusedAsync(OutboundRefusalNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (options.Current.NoticesGcId is not { } gcId || gcId == Guid.Empty)
        {
            Interlocked.Increment(ref _skipped);
            if (logger is not null) NoTargetGc(logger);
            return;
        }

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("notice", ApprovalRefusedKind);
            w.WriteString("agent_id", notice.AgentId);
            w.WriteString("message_id", notice.MessageId);
            w.WriteString("reason_code", notice.ReasonCode);
            w.WriteString("sender_domain", notice.SenderDomain);
            w.WriteNumber("recipient_count", notice.RecipientCount);
            w.WriteString("at", notice.At);
            w.WriteEndObject();
        }

        await appender.AppendAsync(new AppendRequest(
            gcId, GcEventKinds.System, AuthorKind.System, InboxGcPoster.SystemAuthorId, SourceTrust.System, "mail.orf:" + notice.MessageId.ToString("N"))
        {
            PayloadJson = Encoding.UTF8.GetString(stream.ToArray()),
        }, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An outbound mail owner notice was skipped: mail.notices.gc_id is not set.")]
    private static partial void NoTargetGc(ILogger logger);
}
