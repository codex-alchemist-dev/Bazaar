using Bazaar.Secrets;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Mail;
using Bazaar.Server.MailInbound;
using Bazaar.Server.MailOutbound;

namespace Bazaar.Server.MailRuntime.Tests;

internal sealed class RecordingAppender : IMailGcAppender
{
    public List<AppendRequest> Requests { get; } = [];

    public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(new AppendResult(request.GcId, Requests.Count, Guid.NewGuid(), false, DateTimeOffset.UnixEpoch));
    }
}

internal sealed class ScriptedWorker(Func<int, Task<InboundPassResult>> pass) : IInboundMailWorker
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<InboundPassResult> RunPassAsync(CancellationToken ct) => pass(Interlocked.Increment(ref _calls));
}

internal sealed class ScriptedPipeline(Func<int, int, Task<int>> resume) : IOutboundMailPipeline
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public int LastMax { get; private set; }

    public Task<int> ResumeDueAsync(int max, CancellationToken ct)
    {
        LastMax = max;
        return resume(Interlocked.Increment(ref _calls), max);
    }

    public Task<OutboundSendResult> SendAsync(OutboundRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<OutboundSendResult?> ResumeAsync(string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> RecordBounceAsync(string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeCatalog : IMailBackendCatalog
{
    public int Refreshes { get; private set; }

    public IMailBackend? Resolve(string domain) => null;

    public Task<MailBackendRefreshResult> RefreshAsync(CancellationToken ct)
    {
        Refreshes++;
        return Task.FromResult(new MailBackendRefreshResult(0, 0, []));
    }
}

/// <summary>Real (non fail-closed) stand-ins so readiness passes.</summary>
internal sealed class OkDirectory : IInboundMailboxDirectory
{
    public Task<IReadOnlyList<InboundMailboxBinding>> ListActiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InboundMailboxBinding>>([]);
}

internal sealed class OkSealer : IMailBodySealer
{
    public ValueTask<SealedBody> SealAsync(Guid personId, ReadOnlyMemory<byte> raw, CancellationToken ct) => ValueTask.FromResult(new SealedBody(Guid.Empty, [], []));

    public ValueTask<byte[]> OpenAsync(Guid personId, SealedBody sealedBody, CancellationToken ct) => ValueTask.FromResult<byte[]>([]);
}

internal sealed class OkSessions : IMailReaderSessions
{
    public Task<ReaderSessionHandle> StartAsync(ReaderSessionRequest request, CancellationToken ct) => Task.FromResult(new ReaderSessionHandle(Guid.NewGuid()));

    public Task EndAsync(ReaderSessionHandle session, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class OkTaint : IReaderSessionTaint
{
    public Task MarkHardAsync(Guid sessionId, Guid mailMessageId, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class OkReader : IMailTriageReader
{
    public Task<MailTriageHandover> TriageAsync(ReaderSessionHandle session, MailReaderInput input, CancellationToken ct) =>
        Task.FromResult(new MailTriageHandover(input.MailMessageId, MailTriageVerdict.Routine, []));
}

internal sealed class OkGateway : IMailApprovalGateway
{
    public Task<Guid> RequestAsync(MailApprovalRequest request, CancellationToken ct) => Task.FromResult(Guid.NewGuid());

    public Task<MailApprovalStatus> GetStatusAsync(Guid approvalId, CancellationToken ct) => Task.FromResult(MailApprovalStatus.Pending);

    public Task<bool> ConsumeAsync(Guid approvalId, CancellationToken ct) => Task.FromResult(false);
}

internal sealed class OkProtector : IMailBodyProtector
{
    public ValueTask<ProtectedBody> ProtectAsync(string agentId, ReadOnlyMemory<byte> plaintext, CancellationToken ct) => ValueTask.FromResult(new ProtectedBody(Guid.Empty, [], []));

    public ValueTask<byte[]> UnprotectAsync(ProtectedBody body, CancellationToken ct) => ValueTask.FromResult<byte[]>([]);
}

internal sealed class FakeSecretStore(Dictionary<string, string> values) : ISecretStore
{
    public List<string> Requested { get; } = [];

    public Task PutAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretValue value, CancellationToken ct) => throw new NotSupportedException();

    public Task<SecretValue> GetAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretPurpose purpose, CancellationToken ct)
    {
        Requested.Add(name.Value);
        return values.TryGetValue(name.Value, out var text)
            ? Task.FromResult(SecretValue.FromText(text))
            : throw new KeyNotFoundException();
    }

    public Task DeleteAsync(SecretCaller caller, SecretScope scope, SecretName name, CancellationToken ct) => throw new NotSupportedException();

    public Task CryptoEraseAsync(SecretCaller caller, AgentId agent, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class ListDomainSource(params MailDomainConfig[] rows) : IMailDomainSource
{
    public Task<IReadOnlyList<MailDomainConfig>> ListActiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MailDomainConfig>>(rows);
}
