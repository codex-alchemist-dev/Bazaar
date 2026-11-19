using Bazaar.Server.Mail;
using Bazaar.Server.Mail.Parsing;
using Bazaar.Server.Mail.Quarantine;

namespace Bazaar.Server.MailInbound;

/// <summary>
/// The inbound pipeline (doc 22, inbound flow steps 1 to 7), in two stages. Stage A stores every pulled message (parse, screen, encrypt, insert in state <c>received</c>,
/// and admit a held one to quarantine), because a back end may mark mail seen when it is pulled and never offer it again. Stage B finishes stored messages, oldest first, at
/// most one batch per pass: announce, reader triage, deliver or escalate. Every step is idempotent, so the flow is at-least-once with dedupe and a crash resumes from the
/// stored row, not from a redelivery. The first stage-B failure ends the mailbox's pass, so a later message never overtakes an earlier one. Nothing is dropped silently.
/// </summary>
/// <remarks>
/// Held mail never reaches the GC with content: the only post is <see cref="QuarantineNotice"/> (a count, the reference and closed reason codes). Clean mail posts
/// <see cref="ExternalMailEntry"/> (no subject, address or body field exists), then a hard-tainted reader session triages it into a <see cref="MailTriageHandover"/>.
/// The reader's only escalation is quarantine admission as a reader; the pipeline has no release path at all. A hand-over that is invalid is treated as an escalation.
/// </remarks>
public sealed class InboundMailPipeline(
    IMailMessageStore messages,
    IMailQuarantineStore quarantine,
    IMailBodySealer sealer,
    MailScreeningService screening,
    MailScreeningOptions screeningOptions,
    IInboxGcPoster gc,
    IMailReaderSessions readerSessions,
    IReaderSessionTaint taint,
    IMailTriageReader reader,
    InboundMailOptions? options = null,
    IInboundMailEvents? events = null) : IInboundMailPipeline
{
    private readonly InboundMailOptions _options = options ?? new InboundMailOptions();
    private readonly IInboundMailEvents _events = events ?? NullInboundMailEvents.Instance;
    private readonly MimeMailParser _parser = new(screeningOptions.Limits);

    /// <inheritdoc />
    public async Task<MailboxPassResult> ProcessMailboxAsync(InboundMailboxBinding mailbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        _options.Validate();
        var counts = new Counts();

        IReadOnlyList<RawMail> pulled = [];
        try
        {
            pulled = await mailbox.Backend.ReceiveAsync(mailbox.Box, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing was handed over, so nothing is lost: the back end still holds the mail. The outage is the result. Messages stored by an
            // earlier pass and not finished are still worked below.
            counts.Failures.Add(new InboundFailure("backend_unavailable:" + ex.GetType().Name, null));
            _events.Report(new InboundEvent("backend_unavailable", mailbox.MailboxId, null, ex.GetType().Name));
        }

        // Stage A: store every pulled message before anything else. Some back ends (IMAP) mark mail seen when it is pulled, so a message that is not
        // stored here is gone; the heavy work is bounded in stage B, never the storing. Oldest first; the back end id breaks ties.
        counts.Pulled = pulled.Count;
        var ordered = pulled.OrderBy(m => m.ReceivedAt).ThenBy(m => m.BackendId, StringComparer.Ordinal).ToList();
        foreach (var raw in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                counts.Add(await IngestAsync(mailbox, raw, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                counts.Failures.Add(new InboundFailure(ex.GetType().Name, InboundRef.Of(raw.BackendId)));
                _events.Report(new InboundEvent("failed", mailbox.MailboxId, null, ex.GetType().Name));
            }
        }

        // Stage B: finish stored messages oldest first, at most one batch per pass. The first failure ends the pass so a later message never
        // overtakes an earlier one; everything not finished stays stored and is counted as deferred.
        try
        {
            var pending = await messages.ListPendingAsync(mailbox.MailboxId, _options.MaxBatchSize, ct).ConfigureAwait(false);
            foreach (var message in pending)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    counts.Add(await FinishAsync(mailbox, message, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    counts.Failures.Add(new InboundFailure(ex.GetType().Name, "msg:" + message.Id.ToString("N")[..12]));
                    _events.Report(new InboundEvent("failed", mailbox.MailboxId, message.Id, ex.GetType().Name));
                    break;
                }
            }

            counts.Deferred = await messages.CountPendingAsync(mailbox.MailboxId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            counts.Failures.Add(new InboundFailure("pending_unavailable:" + ex.GetType().Name, null));
            _events.Report(new InboundEvent("pending_unavailable", mailbox.MailboxId, null, ex.GetType().Name));
        }

        return counts.ToResult(mailbox.MailboxId);
    }

    /// <summary>Stage A for one message: parse with limits, run the screens, encrypt and store. A message a screen holds is admitted to quarantine here.</summary>
    private async Task<InboundDisposition> IngestAsync(InboundMailboxBinding mailbox, RawMail raw, CancellationToken ct)
    {
        // Steps 2 to 4: a message that breaks a limit or cannot be parsed is held, never skipped.
        var screened = await ScreenAsync(raw, ct).ConfigureAwait(false);

        // Step 1 (dedupe) and storage: the row is written, encrypted, before anything else acts on the message.
        var inserted = await messages.InsertReceivedAsync(
            new NewInboundMessage(
                Guid.NewGuid(), mailbox.MailboxId, screened.RfcMessageId, raw.BackendId, screened.SenderDomain, screened.AuthSummary,
                screened.Codes, raw.ReceivedAt, sealer.Seal(mailbox.PersonId, raw.Bytes)),
            ct).ConfigureAwait(false);
        if (inserted.Outcome == InboundInsertOutcome.Duplicate)
        {
            // Whether the earlier copy is finished or still pending, stage B owns it; this delivery adds nothing.
            _events.Report(new InboundEvent("duplicate", mailbox.MailboxId, inserted.Id, null));
            return InboundDisposition.Duplicate;
        }

        if (screened.Codes.Count == 0)
        {
            return InboundDisposition.Stored;
        }

        await HoldAsync(mailbox, ItemOf(mailbox, inserted.Id, screened.SenderDomain, screened.Codes, screened.AuthSummary, raw.ReceivedAt), QuarantineActor.System, ct)
            .ConfigureAwait(false);
        return InboundDisposition.Quarantined;
    }

    /// <summary>Stage B for one stored message: announce it, have a hard-tainted reader triage it, then deliver or escalate.</summary>
    private async Task<InboundDisposition> FinishAsync(InboundMailboxBinding mailbox, PendingInboundMessage message, CancellationToken ct)
    {
        var item = ItemOf(mailbox, message.Id, message.SenderDomain, message.ScreenCodes, message.AuthSummary, message.ReceivedAt);
        if (message.ScreenCodes.Count > 0)
        {
            // A screen held it but a crash came before admission: finish the hold.
            await HoldAsync(mailbox, item, QuarantineActor.System, ct).ConfigureAwait(false);
            return InboundDisposition.Quarantined;
        }

        // Step 6: announce the clean message. The entry has no field for content; the post is idempotent on the mail message id.
        var gcMessageId = await gc.PostExternalMailAsync(
            new ExternalMailEntry(mailbox.InboxGcId, mailbox.MailboxId, message.Id, message.SenderDomain, message.ReceivedAt, []), ct).ConfigureAwait(false);
        await messages.RecordGcMessageAsync(message.Id, gcMessageId, ct).ConfigureAwait(false);

        // Step 5: a reader session reads the message. It is hard-tainted before it sees any content.
        var parsed = _parser.Parse(sealer.Open(mailbox.PersonId, message.Body));
        var handover = await TriageAsync(mailbox, message.Id, parsed, ct).ConfigureAwait(false);
        if (handover.Verdict == MailTriageVerdict.Escalate)
        {
            // The reader can add to quarantine and nothing more; only the owner releases.
            var escalated = item with { ReasonCodes = [.. handover.ReasonCodes.Select(c => "reader:" + c)] };
            await HoldAsync(mailbox, escalated, QuarantineActor.Reader, ct).ConfigureAwait(false);
            return InboundDisposition.Quarantined;
        }

        await gc.PostTriageHandoverAsync(mailbox.InboxGcId, handover, ct).ConfigureAwait(false);
        await messages.MarkDeliveredAsync(message.Id, ct).ConfigureAwait(false);
        _events.Report(new InboundEvent("delivered", mailbox.MailboxId, message.Id, null));
        return InboundDisposition.Delivered;
    }

    private static QuarantineItem ItemOf(
        InboundMailboxBinding mailbox, Guid id, string senderDomain, IReadOnlyList<string> codes, string authSummary, DateTimeOffset receivedAt) =>
        new(id, mailbox.PersonId, senderDomain, codes, authSummary, receivedAt, QuarantineState.Held);

    private async Task<Screened> ScreenAsync(RawMail raw, CancellationToken ct)
    {
        ParsedMail parsed;
        try
        {
            parsed = _parser.Parse(raw.Bytes);
        }
        catch (MailLimitException ex)
        {
            return Screened.Held("Limit_" + ex.Limit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Malformed MIME is hostile input, not a server fault: the message is held and the pass goes on.
            return Screened.Held("MalformedMessage");
        }

        var (auth, authSummary) = InboundAuthReader.Read(parsed.AuthenticationResults);
        var domain = SenderDomainOf(parsed.FromAddress);
        var rfcId = parsed.MessageId is { Length: > 0 and <= 512 } id ? id : null;
        var codes = new List<string>();
        if (domain is null)
        {
            codes.Add("MissingSender");
        }

        var attachments = new MimeAttachmentBytes(raw.Bytes);
        var decision = await screening.ScreenAsync(parsed, raw.Bytes.Length, auth, attachments.Read, ct: ct).ConfigureAwait(false);
        codes.AddRange(decision.Reasons.Select(r => r.ToString()));
        return new Screened(parsed, rfcId, domain ?? InboundMailOptions.UnknownSenderDomain, authSummary, codes);
    }

    private static string? SenderDomainOf(string? fromAddress) =>
        EmailAddressParser.TryParse(fromAddress, out var address) && address!.Domain.Length <= 253 ? address.Domain : null;

    private async Task<MailTriageHandover> TriageAsync(InboundMailboxBinding mailbox, Guid id, ParsedMail parsed, CancellationToken ct)
    {
        var session = await readerSessions.StartAsync(new ReaderSessionRequest(id, mailbox.MailboxId, mailbox.PersonId), ct).ConfigureAwait(false);
        try
        {
            await taint.MarkHardAsync(session.SessionId, id, ct).ConfigureAwait(false);
            var input = new MailReaderInput(
                id, parsed.FromDisplayName, parsed.Subject, parsed.TextBody, parsed.HtmlBody,
                [.. parsed.Attachments.Select(a => a.FileName ?? string.Empty)]);
            var handover = await reader.TriageAsync(session, input, ct).ConfigureAwait(false);

            // Reader output is untrusted: a wrong message id, an unknown code or a bad verdict fails closed into quarantine.
            return handover is not null && handover.MailMessageId == id && handover.IsValid()
                ? handover
                : new MailTriageHandover(id, MailTriageVerdict.Escalate, [MailTriageReasonCodes.Unclear]);
        }
        finally
        {
            await readerSessions.EndAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HoldAsync(InboundMailboxBinding mailbox, QuarantineItem item, QuarantineActor actor, CancellationToken ct)
    {
        var result = await quarantine.AdmitAsync(item, actor, ct).ConfigureAwait(false);

        // A redelivery finds the item already admitted; that is success. Any other refusal is a fault the pass reports.
        var alreadyHeld = result.Outcome == QuarantineTransitionOutcome.IllegalTransition && result.Item is not null;
        if (!result.IsAllowed && !alreadyHeld)
        {
            throw new InvalidOperationException("quarantine_admit_refused:" + result.Outcome);
        }

        var held = await quarantine.CountHeldAsync(ct).ConfigureAwait(false);
        await gc.PostQuarantineNoticeAsync(new QuarantineNotice(mailbox.InboxGcId, item.Id, item.ReasonCodes, held), ct).ConfigureAwait(false);
        _events.Report(new InboundEvent("quarantined", mailbox.MailboxId, item.Id, null));
    }

    private sealed record Screened(ParsedMail? Parsed, string? RfcMessageId, string SenderDomain, string AuthSummary, List<string> Codes)
    {
        public static Screened Held(string code) => new(null, null, InboundMailOptions.UnknownSenderDomain, "spf=none dkim=none dmarc=none", [code]);
    }

    private sealed class Counts
    {
        public int Pulled;
        public int Delivered;
        public int Quarantined;
        public int Duplicates;
        public int Deferred;
        public List<InboundFailure> Failures { get; } = [];

        public void Add(InboundDisposition disposition)
        {
            switch (disposition)
            {
                case InboundDisposition.Stored: break;
                case InboundDisposition.Delivered: Delivered++; break;
                case InboundDisposition.Quarantined: Quarantined++; break;
                case InboundDisposition.Duplicate: Duplicates++; break;
            }
        }

        public MailboxPassResult ToResult(Guid mailboxId) => new(mailboxId, Pulled, Delivered, Quarantined, Duplicates, Deferred, [.. Failures]);
    }
}
