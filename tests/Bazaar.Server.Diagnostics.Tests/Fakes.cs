using System.Security.Cryptography;
using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Grouping;
using Bazaar.Diagnostics.Ingestion;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Diagnostics.Tests;

internal sealed class RecordingAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }

    public IEnumerable<string> Kinds => Entries.Select(entry => entry.Kind);
}

internal sealed class AllowOwnership : IInstallOwnershipResolver
{
    public bool OwnsInstall(Guid installId) => true;
}

internal sealed class ResolvedSymbols : ISymbolicator
{
    public SymbolsState Resolve(CrashReport report) => SymbolsState.Resolved;
}

/// <summary>AES-GCM sealer with one key per install. Destroying a key makes its payloads unreadable, as erasure does.</summary>
internal sealed class FakeSealer : ICrashPayloadSealer
{
    private readonly Dictionary<Guid, byte[]> _keys = [];

    public void DestroyKey(Guid installId) => _keys.Remove(installId);

    public Task<SealedPayload> SealAsync(Guid installId, SealedPayloadKind kind, Guid recordId, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        if (!_keys.TryGetValue(installId, out var key))
        {
            key = RandomNumberGenerator.GetBytes(32);
            _keys[installId] = key;
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext.Span, cipher, tag, Aad(kind, recordId));
        return Task.FromResult(new SealedPayload("key-" + installId.ToString("N")[..8], nonce, cipher, tag));
    }

    public Task<byte[]> OpenAsync(Guid installId, SealedPayloadKind kind, Guid recordId, SealedPayload payload, CancellationToken ct = default)
    {
        if (!_keys.TryGetValue(installId, out var key))
        {
            throw new CryptographicException("The key was destroyed.");
        }

        var plain = new byte[payload.Ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plain, Aad(kind, recordId));
        return Task.FromResult(plain);
    }

    private static byte[] Aad(SealedPayloadKind kind, Guid recordId) => [(byte)kind, .. recordId.ToByteArray()];
}

internal sealed class RecordingFiler : ICrashIssueFiler
{
    private int _next = 100;

    public List<IssueText> Created { get; } = [];

    public List<(int Issue, string Text)> Comments { get; } = [];

    public List<(int Issue, string Text)> Reopened { get; } = [];

    public Exception? FailWith { get; set; }

    public HashSet<int> Deleted { get; } = [];

    public Task<int> CreateIssueAsync(IssueText issue, CancellationToken ct = default)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Created.Add(issue);
        return Task.FromResult(_next++);
    }

    public Task CommentAsync(int issueNumber, string text, CancellationToken ct = default)
    {
        if (Deleted.Contains(issueNumber))
        {
            throw new CrashIssueNotFoundException(issueNumber);
        }

        Comments.Add((issueNumber, text));
        return Task.CompletedTask;
    }

    public Task ReopenAsync(int issueNumber, string text, CancellationToken ct = default)
    {
        if (Deleted.Contains(issueNumber))
        {
            throw new CrashIssueNotFoundException(issueNumber);
        }

        Reopened.Add((issueNumber, text));
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory store with the same commit rules the Postgres store must keep: optimistic version, idempotent report id, one atomic apply.</summary>
internal sealed class InMemoryCrashStore : ICrashIntakeStore, ICrashActionOutbox
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredCrashGroup> _groups = [];
    private readonly Dictionary<Guid, StoredCrashReport> _reports = [];
    private readonly Dictionary<Guid, StoredCrashAttachment> _attachments = [];
    private readonly List<PendingCrashAction> _outbox = [];
    private readonly List<DateOnly> _filings = [];
    private readonly HashSet<Guid> _dead = [];

    /// <summary>Forces this many <see cref="CrashCommitStatus.VersionConflict"/> results before a commit may succeed.</summary>
    public int ForcedConflicts { get; set; }

    public IReadOnlyCollection<StoredCrashReport> Reports => [.. _reports.Values];

    public IReadOnlyCollection<StoredCrashAttachment> Attachments => [.. _attachments.Values];

    public IReadOnlyList<PendingCrashAction> Outbox => [.. _outbox];

    public IReadOnlySet<Guid> Dead => _dead;

    public StoredCrashGroup? Group(string fingerprint) => _groups.GetValueOrDefault(fingerprint);

    public void Seed(StoredCrashGroup group) => _groups[group.State.Fingerprint] = group;

    public Task<StoredCrashGroup?> GetGroupAsync(string fingerprint, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_groups.GetValueOrDefault(fingerprint));
        }
    }

    public Task<int> CountIssueFilingsAsync(DateOnly utcDay, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_filings.Count(day => day == utcDay));
        }
    }

    public Task<bool> HasPendingFileIssueAsync(string fingerprint, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_outbox.Any(row => row.Fingerprint == fingerprint && row.Action is FileIssue && !_dead.Contains(row.Id)));
        }
    }

    public Task<IReadOnlyList<StoredCrashGroup>> ListDeferredGroupsAsync(int max, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredCrashGroup> list = [.. _groups.Values.Where(g => g.State.Status == GroupStatus.FilingDeferred).OrderBy(g => g.State.FirstSeenAt).Take(max)];
            return Task.FromResult(list);
        }
    }

    public Task<CrashCommitStatus> CommitAsync(CrashCommit commit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (ForcedConflicts > 0)
            {
                ForcedConflicts--;
                return Task.FromResult(CrashCommitStatus.VersionConflict);
            }

            if (commit.Report is { } report && _reports.ContainsKey(report.ReportId))
            {
                return Task.FromResult(CrashCommitStatus.DuplicateReport);
            }

            var fingerprint = commit.Group.State.Fingerprint;
            var exists = _groups.TryGetValue(fingerprint, out var current);
            if (commit.ExpectedGroupVersion is null ? exists : !exists || current!.Version != commit.ExpectedGroupVersion)
            {
                return Task.FromResult(CrashCommitStatus.VersionConflict);
            }

            _groups[fingerprint] = commit.Group;
            if (commit.Report is { } storedReport)
            {
                _reports[storedReport.ReportId] = storedReport;
            }

            if (commit.Attachment is { } attachment)
            {
                _attachments[attachment.AttachmentId] = attachment;
            }

            foreach (var action in commit.Outbox)
            {
                _outbox.Add(action);
                if (action.Action is FileIssue)
                {
                    _filings.Add(DateOnly.FromDateTime(action.CreatedAt.UtcDateTime));
                }
            }

            return Task.FromResult(CrashCommitStatus.Committed);
        }
    }

    public Task<IReadOnlyList<PendingCrashAction>> GetDueAsync(DateTimeOffset now, int max, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<PendingCrashAction> due = [.. _outbox.Where(row => row.NotBefore <= now && !_dead.Contains(row.Id)).OrderBy(row => row.CreatedAt).Take(max)];
            return Task.FromResult(due);
        }
    }

    public Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _outbox.RemoveAll(row => row.Id == id);
            return Task.CompletedTask;
        }
    }

    public Task FailAsync(Guid id, DateTimeOffset notBefore, string reason, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _outbox.FindIndex(row => row.Id == id);
            if (index >= 0)
            {
                _outbox[index] = _outbox[index] with { Attempts = _outbox[index].Attempts + 1, NotBefore = notBefore };
            }

            return Task.CompletedTask;
        }
    }

    public Task DeadLetterAsync(Guid id, string reason, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _dead.Add(id);
            return Task.CompletedTask;
        }
    }
}
