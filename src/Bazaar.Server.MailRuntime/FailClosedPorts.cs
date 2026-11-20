using Bazaar.Server.MailInbound;
using Bazaar.Server.MailOutbound;

namespace Bazaar.Server.MailRuntime;

// Defaults for ports whose backing module does not exist yet (doc 22, "Mail runtime", open items). Each refuses to act and says which port it is.
// None of them can approve, decrypt, read or release anything, and none returns a value that lets mail through.

/// <summary>No approvals service is reachable without Bazaar.Server: a request is refused, a status stays Pending and nothing is consumed.</summary>
public sealed class FailClosedMailApprovalGateway : IMailApprovalGateway, IFailClosedMailPort
{
    public string PortName => nameof(IMailApprovalGateway);

    public Task<Guid> RequestAsync(MailApprovalRequest request, CancellationToken ct) => throw new MailPortNotConfiguredException(PortName);

    public Task<MailApprovalStatus> GetStatusAsync(Guid approvalId, CancellationToken ct) => Task.FromResult(MailApprovalStatus.Pending);

    public Task<bool> ConsumeAsync(Guid approvalId, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Sealed storage (doc 04, per-person keys) is not built: the store never gets to write a body.</summary>
public sealed class FailClosedMailBodyProtector : IMailBodyProtector, IFailClosedMailPort
{
    public string PortName => nameof(IMailBodyProtector);

    public ValueTask<ProtectedBody> ProtectAsync(string agentId, ReadOnlyMemory<byte> plaintext, CancellationToken ct) => throw new MailPortNotConfiguredException(PortName);

    public ValueTask<byte[]> UnprotectAsync(ProtectedBody body, CancellationToken ct) => throw new MailPortNotConfiguredException(PortName);
}

/// <summary>Same gap as <see cref="FailClosedMailBodyProtector"/>, for the inbound pipeline: no clear text is ever kept.</summary>
public sealed class FailClosedMailBodySealer : IMailBodySealer, IFailClosedMailPort
{
    public string PortName => nameof(IMailBodySealer);

    public ValueTask<SealedBody> SealAsync(Guid personId, ReadOnlyMemory<byte> raw, CancellationToken ct) => throw new MailPortNotConfiguredException(PortName);

    public ValueTask<byte[]> OpenAsync(Guid personId, SealedBody sealedBody, CancellationToken ct) => throw new MailPortNotConfiguredException(PortName);
}

/// <summary>Reader sessions (doc 15) are not built: no session starts, so no mail text reaches any model.</summary>
public sealed class FailClosedMailReaderSessions : IMailReaderSessions, IFailClosedMailPort
{
    public string PortName => nameof(IMailReaderSessions);

    public Task<ReaderSessionHandle> StartAsync(ReaderSessionRequest request, CancellationToken ct) => throw new MailPortNotConfiguredException(PortName);

    public Task EndAsync(ReaderSessionHandle session, CancellationToken ct) => throw new MailPortNotConfiguredException(PortName);
}

/// <summary>Taint tracking (doc 15, Security.Abstractions) is not built: a reader cannot be marked, so none is used.</summary>
public sealed class FailClosedReaderSessionTaint : IReaderSessionTaint, IFailClosedMailPort
{
    public string PortName => nameof(IReaderSessionTaint);

    public Task MarkHardAsync(Guid sessionId, Guid mailMessageId, CancellationToken ct) => throw new MailPortNotConfiguredException(PortName);
}

/// <summary>The reader model call is not wired: no triage is produced, so nothing is handed to an actor.</summary>
public sealed class FailClosedMailTriageReader : IMailTriageReader, IFailClosedMailPort
{
    public string PortName => nameof(IMailTriageReader);

    public Task<MailTriageHandover> TriageAsync(ReaderSessionHandle session, MailReaderInput input, CancellationToken ct) =>
        throw new MailPortNotConfiguredException(PortName);
}

/// <summary>
/// No source yet maps a mailbox to its person and inbox GC (<c>mail_mailbox</c> has only <c>soul_id</c>), so no mailbox is pulled. Pulling marks
/// mail seen at the provider, which would lose it.
/// </summary>
public sealed class FailClosedInboundMailboxDirectory : IInboundMailboxDirectory, IFailClosedMailPort
{
    public string PortName => nameof(IInboundMailboxDirectory);

    public Task<IReadOnlyList<InboundMailboxBinding>> ListActiveAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InboundMailboxBinding>>([]);
}

/// <summary>No approved-recipient list exists yet: no recipient is approved, so every send needs the owner (Tier 2).</summary>
public sealed class FailClosedApprovedRecipients : IApprovedRecipients, IFailClosedMailPort
{
    public string PortName => nameof(IApprovedRecipients);

    public ValueTask<bool> IsApprovedAsync(string agentId, string address, CancellationToken ct) => ValueTask.FromResult(false);
}
