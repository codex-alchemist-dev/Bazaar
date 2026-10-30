namespace Bazaar.Server.Mail;

// Mail core contracts (docs/design/22-email-subsystem.md, "Data and interfaces"). Protocol-free: back ends live behind
// IMailBackend, one per operator domain. TODO(component): move under the Mail component once PR #125 merges.

/// <summary>A mailbox reference: the operator's domain plus the local part of one agent's address.</summary>
public readonly record struct MailboxRef(string Domain, string LocalPart)
{
    public string Address => $"{LocalPart}@{Domain}";
}

/// <summary>A message exactly as a back end delivered it, before any parsing or screening.</summary>
public sealed record RawMail(string BackendId, MailboxRef Mailbox, ReadOnlyMemory<byte> Bytes, DateTimeOffset ReceivedAt);

/// <summary>An outbound message. The server sets From; the model never does (doc 22, account model).</summary>
public sealed record OutboundMail(string Key, MailboxRef From, IReadOnlyList<string> To, string Subject, string TextBody);

/// <summary>Result of a send. A repeated key returns the first receipt without sending again.</summary>
public sealed record SendReceipt(string IdempotencyKey, string BackendMessageId, bool AlreadySent);

/// <summary>Back end health for the dashboard and the DNS checker.</summary>
public sealed record BackendHealth(bool Reachable, string? Detail);

/// <summary>One back end for one domain. Ingress is pull by default (doc 22 inbound step 1).</summary>
public interface IMailBackend
{
    Task<IReadOnlyList<RawMail>> ReceiveAsync(MailboxRef box, CancellationToken ct);

    Task<SendReceipt> SendAsync(OutboundMail mail, CancellationToken ct);

    Task<BackendHealth> ProbeAsync(CancellationToken ct);
}
