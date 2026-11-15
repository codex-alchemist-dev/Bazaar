using System.Runtime.CompilerServices;
using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Portability.Tests;

/// <summary>Test-only audit sink. Keeps entries in order and can fail every append.</summary>
public sealed class InMemoryAuditSink : IAuditSink
{
    public List<ImportAuditRecord> Records { get; } = [];

    public bool FailAppends { get; set; }

    public Task AppendAsync(ImportAuditRecord record, CancellationToken ct)
    {
        if (FailAppends)
        {
            throw new InvalidOperationException("audit store unavailable");
        }
        Records.Add(record);
        return Task.CompletedTask;
    }
}

/// <summary>Test-only GC log: implements the append and read abstractions over a list. Idempotent by (author, client message id).</summary>
public sealed class InMemoryGcLog : IGcAppender, IGcLogReader
{
    private readonly List<GcEvent> _events = [];
    private readonly Dictionary<(Guid AuthorId, string ClientMsgId), GcEvent> _byKey = [];

    public bool FailAppends { get; set; }

    public int Count => _events.Count;

    public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct)
    {
        if (FailAppends)
        {
            throw new InvalidOperationException("GC append unavailable");
        }

        var results = new List<AppendResult>(requests.Count);
        foreach (var request in requests)
        {
            if (_byKey.TryGetValue((request.AuthorId, request.ClientMsgId), out var original))
            {
                results.Add(new AppendResult(original.GcId, original.Seq, original.EventId, true, original.CreatedAt));
                continue;
            }

            var seq = _events.Count + 1;
            var created = DateTimeOffset.UnixEpoch;
            var ev = new GcEvent(
                request.GcId,
                seq,
                Guid.NewGuid(),
                request.Kind,
                request.AuthorKind,
                request.AuthorId,
                request.SourceTrust,
                false,
                null,
                null,
                null,
                null,
                null,
                request.PayloadJson,
                created);
            _events.Add(ev);
            _byKey[(request.AuthorId, request.ClientMsgId)] = ev;
            results.Add(new AppendResult(ev.GcId, seq, ev.EventId, false, created));
        }
        return Task.FromResult<IReadOnlyList<AppendResult>>(results);
    }

    public async IAsyncEnumerable<GcEvent> ReadAsync(Guid gcId, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        var taken = 0;
        foreach (var ev in _events)
        {
            if (ev.GcId != gcId || ev.Seq <= afterSeq)
            {
                continue;
            }
            if (taken >= limit)
            {
                break;
            }
            ct.ThrowIfCancellationRequested();
            taken++;
            yield return ev;
        }
        await Task.CompletedTask;
    }

    public Task<long> GetHeadSeqAsync(Guid gcId, CancellationToken ct) =>
        Task.FromResult((long)_events.Count(e => e.GcId == gcId));
}

/// <summary>Test-only body codec: "seals" text as UTF-8 bytes with an empty key.</summary>
public sealed class PlainGcBodyCodec : IGcBodyCodec
{
    public ValueTask<MessageBody> SealAsync(Guid authorId, string text, CancellationToken ct) =>
        ValueTask.FromResult(new MessageBody(Encoding.UTF8.GetBytes(text), Guid.Empty));

    public ValueTask<string?> OpenAsync(ReadOnlyMemory<byte> ciphertext, Guid keyId, ReadOnlyMemory<byte> nonce, CancellationToken ct) =>
        ValueTask.FromResult<string?>(Encoding.UTF8.GetString(ciphertext.Span));
}

/// <summary>
/// Test-only git provider for the Vault target. A commit snapshots every file under the checkout path; reads come from the
/// snapshot of the requested commit. Only the members the import uses are implemented.
/// </summary>
public sealed class InMemoryVaultGit : IGitProvider
{
    private readonly Dictionary<string, Dictionary<string, string>> _commits = new(StringComparer.Ordinal);
    private readonly string _root = new('0', 40);
    private string _head;
    private int _counter;

    public InMemoryVaultGit()
    {
        // A fresh repository has a HEAD: an empty root commit.
        _commits[_root] = new Dictionary<string, string>(StringComparer.Ordinal);
        _head = _root;
    }

    /// <summary>Commits made by <see cref="CommitAsync"/>, not counting the root.</summary>
    public int CommitCount => _counter;

    public Task<string> CommitAsync(string checkoutPath, string message, GitIdentity author, CancellationToken ct)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(checkoutPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(checkoutPath, file).Replace('\\', '/');
            snapshot[relative] = File.ReadAllText(file, Encoding.UTF8);
        }

        if (SameSnapshot(_commits[_head], snapshot))
        {
            throw new InvalidOperationException("nothing to commit");
        }

        _counter++;
        var sha = _counter.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);
        _commits[sha] = snapshot;
        _head = sha;
        return Task.FromResult(sha);
    }

    public Task<string> HeadAsync(string checkoutPath, CancellationToken ct) => Task.FromResult(_head);

    public Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, string commitSha, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(_commits[commitSha].Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());

    public Task<string> ReadTextAsync(string repoPath, string commitSha, string path, CancellationToken ct) =>
        Task.FromResult(_commits[commitSha][path]);

    private static bool SameSnapshot(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public Task<string> AddWorktreeAsync(string repoPath, string branch, string worktreePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> PruneWorktreesAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<MergeResult> MergeAsync(string checkoutPath, string branch, GitIdentity author, CancellationToken ct, string? message = null) => throw new NotSupportedException();

    public Task AbortMergeAsync(string checkoutPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(string repoPath, string oldSha, string newSha, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> ResolveCommitAsync(string repoPath, string commitish, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<GitCommitInfo>> ListCommitsAsync(string repoPath, string? excludeSha, string includeSha, CancellationToken ct) => throw new NotSupportedException();

    public Task<RecoveryReport> RecoverAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Test-only writer that silently drops one source key, to show that verify catches a missing record.</summary>
public sealed class LossyImportTargetWriter(IImportTargetWriter inner, string dropKey) : IImportTargetWriter
{
    public Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct) =>
        inner.ReadCommittedSourceKeysAsync(jobId, ct);

    public Task AppendGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) =>
        inner.AppendGcMessagesAsync(jobId, batchIndex, Keep(items), ct);

    public Task WriteVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) =>
        inner.WriteVaultFilesAsync(jobId, batchIndex, Keep(items), ct);

    public Task<IReadOnlyList<string>> ReadGcSourceKeysAsync(string jobId, CancellationToken ct) =>
        inner.ReadGcSourceKeysAsync(jobId, ct);

    public Task<IReadOnlyList<string>> ReadVaultSourceKeysAsync(string jobId, CancellationToken ct) =>
        inner.ReadVaultSourceKeysAsync(jobId, ct);

    private List<ImportItem> Keep(IReadOnlyList<ImportItem> items) =>
        items.Where(i => i.Record.SourceKey != dropKey).ToList();
}
