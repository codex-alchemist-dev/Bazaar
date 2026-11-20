using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.MailOnboarding;

// Contracts for the mail side of agent onboarding (docs/design/22-email-subsystem.md, "Mail onboarding (doc 06)", PROPOSED).

/// <summary>Outcome of <see cref="IMailOnboarding.OnboardAsync"/>.</summary>
public enum MailOnboardingStatus
{
    /// <summary>The inbox GC exists and the mailbox now belongs to the agent.</summary>
    Onboarded,

    /// <summary>Nothing to do: the mailbox already belonged to this agent (the inbox GC was still ensured).</summary>
    AlreadyOnboarded,

    /// <summary>No such mailbox. Nothing was created.</summary>
    MailboxNotFound,

    /// <summary>The mailbox belongs to a different agent. It is never reassigned.</summary>
    MailboxOwnedByOtherAgent,

    /// <summary>The inbox GC could not be ensured (port not wired, or it failed). <c>person_id</c> is left unset; retry later.</summary>
    InboxGcUnavailable,
}

/// <summary>Result of an onboarding call. <see cref="InboxGcId"/> is the deterministic inbox GC id for the agent.</summary>
public sealed record MailOnboardingResult(MailOnboardingStatus Status, Guid InboxGcId);

/// <summary>Links an agent to its mailbox and its inbox GC. Idempotent and safe to call concurrently for the same pair.</summary>
public interface IMailOnboarding
{
    /// <param name="agentId">The agent's principal id, the value stored in <c>mail_mailbox.person_id</c>.</param>
    /// <param name="mailboxId">The <c>mail_mailbox.id</c> to bind.</param>
    Task<MailOnboardingResult> OnboardAsync(Guid agentId, Guid mailboxId, CancellationToken ct);
}

/// <summary>Outcome of <see cref="IMailboxPersonStore.SetPersonAsync"/>.</summary>
public enum MailboxPersonSetResult
{
    /// <summary><c>person_id</c> was NULL and is now the agent.</summary>
    Set,

    /// <summary><c>person_id</c> already was the agent.</summary>
    AlreadySet,

    /// <summary>A different agent owns the mailbox. Nothing changed.</summary>
    OwnedByOther,

    /// <summary>No such mailbox.</summary>
    NotFound,
}

/// <summary>What the store knows about a mailbox: whether it exists and which agent, if any, owns it.</summary>
public readonly record struct MailboxOwner(bool Exists, Guid? PersonId);

/// <summary>Store over <c>mail_mailbox.person_id</c>.</summary>
public interface IMailboxPersonStore
{
    /// <summary>Compare-and-set: sets only when <c>person_id</c> is NULL or already equal; never reassigns.</summary>
    Task<MailboxPersonSetResult> SetPersonAsync(Guid mailboxId, Guid agentId, CancellationToken ct);

    /// <summary>Whether the mailbox exists and who owns it.</summary>
    Task<MailboxOwner> GetOwnerAsync(Guid mailboxId, CancellationToken ct);
}

/// <summary>
/// Ensures an agent's inbox GC exists with the agent as a member. Idempotent: the same <paramref name="gcId"/> twice is one GC. No database or GC
/// types cross this port; the adapter (over GC's provisioner) is added when GC publishes it, and until then the default fails closed.
/// </summary>
public interface IInboxGcPort
{
    /// <returns>True when the GC exists afterwards; false when it could not be ensured.</returns>
    Task<bool> EnsureInboxGcAsync(Guid gcId, Guid agentId, Guid mailboxId, string title, CancellationToken ct);
}

/// <summary>Marker for a default that refuses to act because its backing module does not exist yet.</summary>
public interface IFailClosedMailOnboardingPort
{
}

/// <summary>The deterministic inbox GC id: one per agent, so retries and concurrent calls converge on the same GC.</summary>
public static class InboxGcId
{
    /// <summary>The GC type key, seeded in <c>gc_type</c>.</summary>
    public const string TypeKey = "inbox";

    /// <summary>Metadata key that records the mailbox on the GC.</summary>
    public const string MailboxMetadataKey = "mail.mailbox_id";

    /// <summary>First 16 bytes of SHA-256 over <c>mail-inbox-gc:{agentId:D}</c>, with version 5 and RFC 4122 variant bits set.</summary>
    public static Guid For(Guid agentId)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"mail-inbox-gc:{agentId:D}"), hash);
        Span<byte> b = hash[..16];
        b[6] = (byte)((b[6] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b, bigEndian: true);
    }
}
