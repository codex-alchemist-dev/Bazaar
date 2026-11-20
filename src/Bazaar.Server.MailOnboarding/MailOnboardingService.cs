using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.MailOnboarding;

/// <summary>
/// The mail side of agent onboarding (docs/design/22-email-subsystem.md, "Mail onboarding (doc 06)"). Order matters: the inbox GC is ensured
/// first and <c>person_id</c> is set second, because the inbound directory lists a mailbox only when both exist, so it never sees a half state.
/// Both steps are idempotent, so a failure in step 2 after step 1 is safe to retry. Audit rows carry ids and the status only, never an address.
/// </summary>
public sealed class MailOnboardingService(
    IMailboxPersonStore mailboxes,
    IInboxGcPort inboxGc,
    IAuditLog? audit = null,
    TimeProvider? timeProvider = null) : IMailOnboarding
{
    public const string AuditActor = "mail.onboarding";

    private const string _inboxTitle = "Inbox";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<MailOnboardingResult> OnboardAsync(Guid agentId, Guid mailboxId, CancellationToken ct)
    {
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("An agent id is required.", nameof(agentId));
        }

        var gcId = InboxGcId.For(agentId);

        // Refuse early: a mailbox that is missing or taken must not cause a GC to be created for it.
        var owner = await mailboxes.GetOwnerAsync(mailboxId, ct).ConfigureAwait(false);
        if (!owner.Exists)
        {
            return await DoneAsync(MailOnboardingStatus.MailboxNotFound, agentId, mailboxId, gcId, ct).ConfigureAwait(false);
        }

        if (owner.PersonId is { } other && other != agentId)
        {
            return await DoneAsync(MailOnboardingStatus.MailboxOwnedByOtherAgent, agentId, mailboxId, gcId, ct).ConfigureAwait(false);
        }

        bool ensured;
        try
        {
            ensured = await inboxGc.EnsureInboxGcAsync(gcId, agentId, mailboxId, _inboxTitle, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The port's failure detail is its own to log; the mailbox stays unbound and the call can be retried.
            ensured = false;
        }

        if (!ensured)
        {
            return await DoneAsync(MailOnboardingStatus.InboxGcUnavailable, agentId, mailboxId, gcId, ct).ConfigureAwait(false);
        }

        var set = await mailboxes.SetPersonAsync(mailboxId, agentId, ct).ConfigureAwait(false);
        var status = set switch
        {
            MailboxPersonSetResult.Set => MailOnboardingStatus.Onboarded,
            MailboxPersonSetResult.AlreadySet => MailOnboardingStatus.AlreadyOnboarded,
            MailboxPersonSetResult.OwnedByOther => MailOnboardingStatus.MailboxOwnedByOtherAgent,
            _ => MailOnboardingStatus.MailboxNotFound,
        };
        return await DoneAsync(status, agentId, mailboxId, gcId, ct).ConfigureAwait(false);
    }

    private async Task<MailOnboardingResult> DoneAsync(MailOnboardingStatus status, Guid agentId, Guid mailboxId, Guid gcId, CancellationToken ct)
    {
        if (audit is not null)
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteString("agent_id", agentId);
                json.WriteString("mailbox_id", mailboxId);
                json.WriteString("inbox_gc_id", gcId);
                json.WriteEndObject();
            }

            await audit.AppendAsync(
                new AuditEntry(_time.GetUtcNow(), AuditActor, "mail.onboarding." + status.ToString().ToLowerInvariant(), Encoding.UTF8.GetString(stream.ToArray())),
                ct).ConfigureAwait(false);
        }

        return new MailOnboardingResult(status, gcId);
    }
}
