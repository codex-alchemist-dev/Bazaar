using System.Security.Cryptography;
using Bazaar.Server.Mail;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.MailOutbound.Tests;

/// <summary>A clock a test moves by hand.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class FakeAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        lock (Entries) Entries.Add(e);
        return Task.CompletedTask;
    }

    public IEnumerable<string> Kinds => Entries.Select(e => e.Kind);
}

/// <summary>Stands in for the approvals store: one request per call, decided by the test.</summary>
public sealed class FakeApprovals : IMailApprovalGateway
{
    private readonly Dictionary<Guid, MailApprovalStatus> _status = [];

    public List<(Guid Id, MailApprovalRequest Request)> Requests { get; } = [];

    public int ConsumeCalls { get; private set; }

    public Task<Guid> RequestAsync(MailApprovalRequest request, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _status[id] = MailApprovalStatus.Pending;
        Requests.Add((id, request));
        return Task.FromResult(id);
    }

    public Task<MailApprovalStatus> GetStatusAsync(Guid approvalId, CancellationToken ct) => Task.FromResult(_status[approvalId]);

    public Task<bool> ConsumeAsync(Guid approvalId, CancellationToken ct)
    {
        ConsumeCalls++;
        if (_status[approvalId] != MailApprovalStatus.Approved) return Task.FromResult(false);
        _status[approvalId] = MailApprovalStatus.Consumed;
        return Task.FromResult(true);
    }

    public void Set(Guid id, MailApprovalStatus status) => _status[id] = status;
}

/// <summary>A back end that, like the real ones, sends once per idempotency key (Idempotency-Key header, deterministic Message-ID).</summary>
public sealed class FakeBackend : IMailBackend
{
    private readonly Dictionary<string, SendReceipt> _sent = [];

    /// <summary>How many calls failed before one is allowed to work.</summary>
    public int FailNext { get; set; }

    public int Calls { get; private set; }

    public List<OutboundMail> Mails { get; } = [];

    /// <summary>Distinct keys the back end actually delivered.</summary>
    public int Delivered => _sent.Count;

    public Task<IReadOnlyList<RawMail>> ReceiveAsync(MailboxRef box, CancellationToken ct) => Task.FromResult<IReadOnlyList<RawMail>>([]);

    public Task<SendReceipt> SendAsync(OutboundMail mail, CancellationToken ct)
    {
        Calls++;
        Mails.Add(mail);
        if (FailNext > 0)
        {
            FailNext--;
            throw new InvalidOperationException("The back end is down.");
        }

        if (_sent.TryGetValue(mail.Key, out var prior)) return Task.FromResult(prior with { AlreadySent = true });
        var receipt = new SendReceipt(mail.Key, "backend-" + (_sent.Count + 1), AlreadySent: false);
        _sent[mail.Key] = receipt;
        return Task.FromResult(receipt);
    }

    public Task<BackendHealth> ProbeAsync(CancellationToken ct) => Task.FromResult(new BackendHealth(true, null));
}

public sealed class FakeResolver(IMailBackend? backend) : IMailBackendResolver
{
    public IMailBackend? Resolve(string domain) => backend;
}

/// <summary>Counts policy evaluations: each one takes a quota slot, so the pipeline must make exactly one per send.</summary>
public sealed class CountingPolicy(IOutboundMailPolicy inner) : IOutboundMailPolicy
{
    public int Evaluations { get; private set; }

    public Task<OutboundDecision> EvaluateAsync(OutboundRequest request, CancellationToken ct)
    {
        Evaluations++;
        return inner.EvaluateAsync(request, ct);
    }
}

/// <summary>A process crash: thrown by the store decorator, never caught by the pipeline.</summary>
public sealed class SimulatedCrashException : Exception;

/// <summary>Lets the first <c>SurviveUpdates</c> updates through, then crashes on the next one, as a process would die between two writes.</summary>
public sealed class CrashingStore(IOutboundMailStore inner, int surviveUpdates) : IOutboundMailStore
{
    private int _updates;

    public Task<StoredOutbound?> FindAsync(string idempotencyKey, CancellationToken ct) => inner.FindAsync(idempotencyKey, ct);

    public Task<(StoredOutbound Record, bool Created)> TryInsertAsync(StoredOutbound record, CancellationToken ct) => inner.TryInsertAsync(record, ct);

    public Task<bool> TryUpdateAsync(StoredOutbound expected, StoredOutbound updated, CancellationToken ct) =>
        ++_updates > surviveUpdates ? throw new SimulatedCrashException() : inner.TryUpdateAsync(expected, updated, ct);

    public Task<IReadOnlyList<string>> ListDueAsync(DateTimeOffset now, DateTimeOffset staleSendingBefore, int max, CancellationToken ct) =>
        inner.ListDueAsync(now, staleSendingBefore, max, ct);
}

/// <summary>AES-GCM body protection for tests that need a real cipher, so a stored body can be shown not to be plaintext.</summary>
public sealed class TestBodyProtector : IMailBodyProtector
{
    private static readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public ProtectedBody Protect(string agentId, ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length + 16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length, 16));
        return new ProtectedBody(Guid.NewGuid(), ciphertext, nonce);
    }

    public byte[] Unprotect(ProtectedBody body)
    {
        var length = body.Ciphertext.Length - 16;
        var plaintext = new byte[length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(body.Nonce, body.Ciphertext.AsSpan(0, length), body.Ciphertext.AsSpan(length, 16), plaintext);
        return plaintext;
    }
}
