using System.Text;
using System.Text.Json;
using Bazaar.Server.Mail;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.MailOutbound;

/// <summary>
/// The outbound pipeline (docs/design/22-email-subsystem.md, outbound flow and durable outbound store option (a)). It is the caller of
/// <see cref="IOutboundMailPolicy"/>: evaluate once per send, then refuse, pause, hold for the owner, or send. It adds no approval, rate limit,
/// kill switch or audit mechanism; each is a port onto the existing module.
/// </summary>
/// <remarks>
/// A stored row is written before the back end is called, so an idempotency key survives a restart. A key that is already stored is advanced and never
/// re-evaluated, because evaluation consumes a quota slot. Delivery is at least once toward the back end and exactly once for the receipt: the back
/// ends send an Idempotency-Key header or a deterministic Message-ID, which makes a resend after a crash between the call and the receipt harmless.
/// Audit rows carry ids, counts and reason codes only, never an address, a subject or a body.
/// </remarks>
public sealed class OutboundMailPipeline(
    IOutboundMailPolicy policy,
    IOutboundMailStore store,
    IMailApprovalGateway approvals,
    IMailBackendResolver backends,
    IAuditLog audit,
    OutboundPipelineOptions? options = null,
    TimeProvider? timeProvider = null) : IOutboundMailPipeline
{
    public const string AuditActor = "mail.outbound";

    private readonly OutboundPipelineOptions _options = options ?? new OutboundPipelineOptions();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<OutboundSendResult> SendAsync(OutboundRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A stored key is advanced, never re-evaluated: evaluation takes a quota slot and approval must not be asked for twice.
        var key = OutboundIdempotency.Derive(request);
        if (await store.FindAsync(key, ct).ConfigureAwait(false) is { } existing)
        {
            return await AdvanceAsync(existing, repeated: true, ct).ConfigureAwait(false);
        }

        var decision = await policy.EvaluateAsync(request, ct).ConfigureAwait(false);
        switch (decision.Kind)
        {
            case OutboundDecisionKind.Refuse:
                await AuditAsync("refused", request.AgentId, string.Empty, null, null, decision.Reason, 0, request, ct).ConfigureAwait(false);
                return new OutboundSendResult(OutboundSendStatus.Refused, decision.Reason, string.Empty);
            case OutboundDecisionKind.Pause:
                await AuditAsync("paused", request.AgentId, string.Empty, null, null, decision.Reason, 0, request, ct).ConfigureAwait(false);
                return new OutboundSendResult(OutboundSendStatus.Paused, decision.Reason, string.Empty, RetryAfter: decision.RetryAfter);
        }

        var needsApproval = decision.Kind == OutboundDecisionKind.NeedsOwnerApproval;
        var now = _time.GetUtcNow();
        var fresh = new StoredOutbound(
            decision.IdempotencyKey,
            Guid.NewGuid(),
            ApprovalId: null,
            needsApproval ? OutboundStatus.AwaitingApproval : OutboundStatus.Sending,
            Attempts: 0,
            NextAttemptAt: null,
            decision.Footer,
            decision.Tier,
            decision.Reason,
            now,
            request);

        // The row goes in before the approval is requested and before the back end is called. If the process dies from here on, a resume finds it.
        var (record, created) = await store.TryInsertAsync(fresh, ct).ConfigureAwait(false);
        if (!created)
        {
            // A racing caller stored the same key first; its policy evaluation stands.
            return await AdvanceAsync(record, repeated: true, ct).ConfigureAwait(false);
        }

        await AuditAsync(needsApproval ? "queued_for_approval" : "sending", request.AgentId, record.IdempotencyKey, record.MessageId, null, decision.Reason,
            0, request, ct).ConfigureAwait(false);
        return await AdvanceAsync(record, repeated: false, ct).ConfigureAwait(false);
    }

    public async Task<OutboundSendResult?> ResumeAsync(string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var record = await store.FindAsync(idempotencyKey, ct).ConfigureAwait(false);
        return record is null ? null : await AdvanceAsync(record, repeated: false, ct).ConfigureAwait(false);
    }

    public async Task<int> ResumeDueAsync(int max, CancellationToken ct)
    {
        if (max <= 0) return 0;
        var now = _time.GetUtcNow();
        var keys = await store.ListDueAsync(now, now - _options.EffectiveStaleAfter, max, ct).ConfigureAwait(false);
        var advanced = 0;
        foreach (var key in keys)
        {
            if (await ResumeAsync(key, ct).ConfigureAwait(false) is not null) advanced++;
        }

        return advanced;
    }

    public async Task<bool> RecordBounceAsync(string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (await store.FindAsync(idempotencyKey, ct).ConfigureAwait(false) is not { Status: OutboundStatus.Sent } sent) return false;
        var updated = sent with { Status = OutboundStatus.Bounced, UpdatedAt = _time.GetUtcNow() };
        if (!await store.TryUpdateAsync(sent, updated, ct).ConfigureAwait(false)) return false;
        await AuditAsync("bounced", sent.Request?.AgentId ?? string.Empty, sent.IdempotencyKey, sent.MessageId, sent.ApprovalId, sent.Reason, sent.Attempts,
            sent.Request, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Moves a stored send one step forward and reports where it stands.</summary>
    private async Task<OutboundSendResult> AdvanceAsync(StoredOutbound record, bool repeated, CancellationToken ct)
    {
        switch (record.Status)
        {
            case OutboundStatus.Sent:
                return Result(record, repeated ? OutboundSendStatus.AlreadySent : OutboundSendStatus.Sent);
            case OutboundStatus.Bounced:
                return Result(record, OutboundSendStatus.Bounced);
            case OutboundStatus.Failed:
                return Result(record, OutboundSendStatus.Failed);
            case OutboundStatus.Rejected:
                return Result(record, OutboundSendStatus.Rejected);
            case OutboundStatus.Expired:
                return Result(record, OutboundSendStatus.Expired);
            case OutboundStatus.AwaitingApproval:
                return await AdvanceApprovalAsync(record, ct).ConfigureAwait(false);
            case OutboundStatus.Pending:
                {
                    var now = _time.GetUtcNow();
                    if (record.NextAttemptAt is { } due && due > now)
                    {
                        return Result(record, OutboundSendStatus.RetryScheduled) with { RetryAfter = due - now };
                    }

                    return await DeliverAsync(record, ct).ConfigureAwait(false);
                }
            default:
                {
                    // Sending: stored, and either in flight now or interrupted by a restart. A row no attempt has claimed yet is delivered
                    // (the compare-and-set on attempts lets only one caller claim it). A claimed row is in flight until it goes stale, then it is resumed.
                    var now = _time.GetUtcNow();
                    var staleAt = record.UpdatedAt + _options.EffectiveStaleAfter;
                    if (record.Attempts > 0 && staleAt > now)
                    {
                        return Result(record, OutboundSendStatus.RetryScheduled) with { RetryAfter = staleAt - now };
                    }

                    return await DeliverAsync(record, ct).ConfigureAwait(false);
                }
        }
    }

    private async Task<OutboundSendResult> AdvanceApprovalAsync(StoredOutbound record, CancellationToken ct)
    {
        if (record.Request is null) return await FailTerminalAsync(record, OutboundStatus.Failed, ct).ConfigureAwait(false);

        var approvalId = record.ApprovalId;
        if (approvalId is null)
        {
            var request = record.Request;
            Guid raised;
            try
            {
                raised = await approvals.RequestAsync(
                    new MailApprovalRequest(
                        request.AgentId, record.IdempotencyKey, record.Tier, record.Reason, request.From, request.Recipients, request.Subject, request.TextBody,
                        record.Footer, request.AttachmentCount),
                    ct).ConfigureAwait(false);
            }
            catch (MailApprovalRefusedException refused)
            {
                // The store will not take this request, so asking again cannot help: end the send for good.
                return await FailTerminalAsync(record, OutboundStatus.Failed, ct, refused.ReasonCode).ConfigureAwait(false);
            }

            var withApproval = record with { ApprovalId = raised, UpdatedAt = _time.GetUtcNow() };
            if (!await store.TryUpdateAsync(record, withApproval, ct).ConfigureAwait(false))
            {
                // Another caller linked an approval first. Read it back; the extra request is never consumed and expires on its own.
                var latest = await store.FindAsync(record.IdempotencyKey, ct).ConfigureAwait(false);
                return latest is null ? Result(record, OutboundSendStatus.AwaitingApproval) : await AdvanceAsync(latest, repeated: false, ct).ConfigureAwait(false);
            }

            await AuditAsync("approval_requested", request.AgentId, record.IdempotencyKey, record.MessageId, raised, record.Reason, record.Attempts, request, ct)
                .ConfigureAwait(false);
            record = withApproval;
            approvalId = raised;
        }

        // One approval per message: it is bound to this key's stored row and no other. The policy is not asked again.
        var status = await approvals.GetStatusAsync(approvalId.Value, ct).ConfigureAwait(false);
        switch (status)
        {
            case MailApprovalStatus.Pending:
                return Result(record, OutboundSendStatus.AwaitingApproval);
            case MailApprovalStatus.Denied:
                return await FailTerminalAsync(record, OutboundStatus.Rejected, ct).ConfigureAwait(false);
            case MailApprovalStatus.Expired:
                return await FailTerminalAsync(record, OutboundStatus.Expired, ct).ConfigureAwait(false);
            default:
                {
                    // Approved, or consumed by an earlier pass that died before it could move the row. Consume (a no-op when already consumed), then
                    // claim the send with a compare-and-set so only one resumer goes on.
                    await approvals.ConsumeAsync(approvalId.Value, ct).ConfigureAwait(false);
                    var claimed = record with { Status = OutboundStatus.Sending, UpdatedAt = _time.GetUtcNow() };
                    if (!await store.TryUpdateAsync(record, claimed, ct).ConfigureAwait(false))
                    {
                        var latest = await store.FindAsync(record.IdempotencyKey, ct).ConfigureAwait(false);
                        return latest is null ? Result(record, OutboundSendStatus.AwaitingApproval) : Result(latest, StatusFor(latest));
                    }

                    await AuditAsync("approved", record.Request.AgentId, record.IdempotencyKey, record.MessageId, approvalId, record.Reason, record.Attempts,
                        record.Request, ct).ConfigureAwait(false);
                    return await DeliverAsync(claimed, ct).ConfigureAwait(false);
                }
        }
    }

    private async Task<OutboundSendResult> DeliverAsync(StoredOutbound record, CancellationToken ct)
    {
        if (record.Request is null) return await FailTerminalAsync(record, OutboundStatus.Failed, ct).ConfigureAwait(false);

        // Claim this attempt: Sending with attempts + 1. A second resumer loses the compare-and-set and does not call the back end.
        var attempt = record.Attempts + 1;
        var claimed = record with { Status = OutboundStatus.Sending, Attempts = attempt, NextAttemptAt = null, UpdatedAt = _time.GetUtcNow() };
        if (!await store.TryUpdateAsync(record, claimed, ct).ConfigureAwait(false))
        {
            var latest = await store.FindAsync(record.IdempotencyKey, ct).ConfigureAwait(false);
            return latest is null ? Result(record, OutboundSendStatus.RetryScheduled) : Result(latest, StatusFor(latest));
        }

        var request = record.Request;
        SendReceipt? receipt = null;
        var failed = false;
        try
        {
            var backend = backends.Resolve(request.From.Domain);
            if (backend is null)
            {
                failed = true;
            }
            else
            {
                // The footer is the policy's, stored with the row. The model never sets or removes it.
                var mail = new OutboundMail(
                    record.IdempotencyKey, request.From, request.Recipients, request.Subject, request.TextBody, record.MessageId, record.Footer);
                receipt = await backend.SendAsync(mail, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
        }

        var now = _time.GetUtcNow();
        if (!failed && receipt is not null)
        {
            var sent = claimed with { Status = OutboundStatus.Sent, BackendMessageId = receipt.BackendMessageId, UpdatedAt = now };
            await store.TryUpdateAsync(claimed, sent, ct).ConfigureAwait(false);
            await AuditAsync("sent", request.AgentId, sent.IdempotencyKey, sent.MessageId, sent.ApprovalId, sent.Reason, attempt, request, ct).ConfigureAwait(false);
            return Result(sent, OutboundSendStatus.Sent) with { Receipt = receipt };
        }

        if (attempt >= _options.EffectiveMaxAttempts)
        {
            return await FailTerminalAsync(claimed, OutboundStatus.Failed, ct).ConfigureAwait(false);
        }

        var delay = _options.DelayAfter(attempt);
        var pending = claimed with { Status = OutboundStatus.Pending, NextAttemptAt = now + delay, UpdatedAt = now };
        await store.TryUpdateAsync(claimed, pending, ct).ConfigureAwait(false);
        await AuditAsync("retry_scheduled", request.AgentId, pending.IdempotencyKey, pending.MessageId, pending.ApprovalId, pending.Reason, attempt, request, ct)
            .ConfigureAwait(false);
        return Result(pending, OutboundSendStatus.RetryScheduled) with { RetryAfter = delay };
    }

    private async Task<OutboundSendResult> FailTerminalAsync(
        StoredOutbound record, OutboundStatus terminal, CancellationToken ct, string? reasonCode = null)
    {
        var updated = record with { Status = terminal, NextAttemptAt = null, UpdatedAt = _time.GetUtcNow() };
        if (await store.TryUpdateAsync(record, updated, ct).ConfigureAwait(false))
        {
            var kind = terminal switch
            {
                OutboundStatus.Rejected => "rejected",
                OutboundStatus.Expired => "expired",
                _ => "failed",
            };
            await AuditAsync(kind, record.Request?.AgentId ?? string.Empty, record.IdempotencyKey, record.MessageId, record.ApprovalId, record.Reason,
                record.Attempts, record.Request, ct, reasonCode).ConfigureAwait(false);
        }

        return Result(updated, StatusFor(updated));
    }

    private static OutboundSendStatus StatusFor(StoredOutbound record) => record.Status switch
    {
        OutboundStatus.Sent => OutboundSendStatus.AlreadySent,
        OutboundStatus.Bounced => OutboundSendStatus.Bounced,
        OutboundStatus.Failed => OutboundSendStatus.Failed,
        OutboundStatus.Rejected => OutboundSendStatus.Rejected,
        OutboundStatus.Expired => OutboundSendStatus.Expired,
        OutboundStatus.AwaitingApproval => OutboundSendStatus.AwaitingApproval,
        _ => OutboundSendStatus.RetryScheduled,
    };

    private static OutboundSendResult Result(StoredOutbound record, OutboundSendStatus status) =>
        new(status, record.Reason, record.IdempotencyKey, record.MessageId, record.ApprovalId, Attempts: record.Attempts);

    /// <summary>Appends one audit row. The payload holds ids, counts and reason codes; a subject, body or address never enters it.</summary>
    private async Task AuditAsync(
        string state, string agentId, string key, Guid? messageId, Guid? approvalId, OutboundReason? reason, int attempts, OutboundRequest? request, CancellationToken ct,
        string? reasonCode = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("agent_id", agentId);
            writer.WriteString("idempotency_key", key);
            if (messageId is { } message) writer.WriteString("message_id", message);
            if (approvalId is { } approval) writer.WriteString("approval_id", approval);
            if (reason is { } code) writer.WriteString("reason", code.ToString());
            if (reasonCode is not null) writer.WriteString("reason_code", reasonCode);
            writer.WriteNumber("attempts", attempts);
            if (request is not null)
            {
                writer.WriteNumber("recipient_count", request.Recipients.Count);
                writer.WriteNumber("attachment_count", request.AttachmentCount);
            }

            writer.WriteEndObject();
        }

        await audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), AuditActor, "mail.outbound." + state, Encoding.UTF8.GetString(stream.ToArray())), ct)
            .ConfigureAwait(false);
    }
}
