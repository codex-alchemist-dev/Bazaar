using System.Runtime.CompilerServices;
using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Portability.Withdrawal.Tests;

internal sealed class RecordingAuditSink : IAuditSink
{
    public List<ImportAuditRecord> Records { get; } = [];

    public Task AppendAsync(ImportAuditRecord record, CancellationToken ct)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }
}

/// <summary>A GC log writer that records appends and answers a repeated (author, key) with the original, like the real one.</summary>
internal sealed class FakeGcLogWriter : IGcLogWriter
{
    private readonly Dictionary<(Guid Author, string Key), AppendResult> _seen = [];
    private long _seq = 100;

    public List<AppendRequest> Written { get; } = [];

    public bool Fail { get; set; }

    public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct)
    {
        if (Fail)
        {
            throw new InvalidOperationException("writer unavailable");
        }

        var results = new List<AppendResult>();
        foreach (var r in requests)
        {
            if (_seen.TryGetValue((r.AuthorId, r.ClientMsgId), out var original))
            {
                results.Add(original with { Duplicate = true });
                continue;
            }
            var result = new AppendResult(r.GcId, ++_seq, Guid.CreateVersion7(), false, DateTimeOffset.UnixEpoch);
            _seen[(r.AuthorId, r.ClientMsgId)] = result;
            Written.Add(r);
            results.Add(result);
        }
        return Task.FromResult<IReadOnlyList<AppendResult>>(results);
    }
}

internal sealed class FakeGcLogReader(IReadOnlyList<GcEvent> events) : IGcLogReader
{
    public async IAsyncEnumerable<GcEvent> ReadAsync(Guid gcId, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in events.Where(e => e.Seq > afterSeq).OrderBy(e => e.Seq).Take(limit))
        {
            yield return e;
        }
        await Task.CompletedTask;
    }

    public Task<long> GetHeadSeqAsync(Guid gcId, CancellationToken ct) => Task.FromResult(events.Count == 0 ? 0 : events.Max(e => e.Seq));
}

internal sealed class FakeVersionSource(IReadOnlyList<GcMessageVersionRecord> versions) : IGcMessageVersionSource
{
    public async IAsyncEnumerable<GcMessageVersionRecord> ReadAsync(Guid gcId, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var v in versions.Where(v => v.Seq > afterSeq).OrderBy(v => v.Seq).Take(limit))
        {
            yield return v;
        }
        await Task.CompletedTask;
    }
}

/// <summary>
/// An in-memory git: commits with full trees and named branches. The main checkout is branch "main". A worktree is a real
/// temporary directory holding its branch's tree; a commit reads the directory back. Only the calls the reverter makes work.
/// </summary>
internal sealed class FakeGit : IGitProvider
{
    public sealed record FakeCommit(string Sha, string[] Parents, string Message, Dictionary<string, string> Tree);

    private readonly Dictionary<string, FakeCommit> _commits = [];
    private readonly List<string> _order = [];
    private readonly Dictionary<string, string> _branches = [];
    private readonly Dictionary<string, string> _worktrees = [];
    private int _n;

    public const string Repo = "repo";

    public string MainHead => _branches["main"];

    public FakeCommit Head => _commits[MainHead];

    public IReadOnlyList<string> Order => _order;

    /// <summary>Adds a commit to main whose tree is the previous tree with <paramref name="changes"/> applied (a null value deletes).</summary>
    public string CommitMain(string message, Dictionary<string, string?> changes)
    {
        var tree = _branches.TryGetValue("main", out var head) ? new Dictionary<string, string>(_commits[head].Tree) : [];
        foreach (var (path, text) in changes)
        {
            if (text is null) tree.Remove(path);
            else tree[path] = text;
        }
        return Add("main", message, tree);
    }

    /// <summary>What the main checkout does when the writer lands a branch: fast-forward to the branch head.</summary>
    public bool FastForwardMain(string branch)
    {
        var target = _branches[branch];
        if (target == MainHead)
        {
            return false;
        }
        _branches["main"] = target;
        return true;
    }

    public bool BranchExists(string branch) => _branches.ContainsKey(branch);

    private string Add(string branch, string message, Dictionary<string, string> tree)
    {
        var sha = "c" + (++_n).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        var parents = _branches.TryGetValue(branch, out var p) ? new[] { p } : [];
        _commits[sha] = new FakeCommit(sha, parents, message, tree);
        _order.Add(sha);
        _branches[branch] = sha;
        return sha;
    }

    public Task<string> AddWorktreeAsync(string repoPath, string branch, string worktreePath, CancellationToken ct)
    {
        _branches.TryAdd(branch, MainHead);
        Directory.CreateDirectory(worktreePath);
        foreach (var (path, text) in _commits[_branches[branch]].Tree)
        {
            var full = Path.Combine(worktreePath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }
        _worktrees[Path.GetFullPath(worktreePath)] = branch;
        return Task.FromResult(worktreePath);
    }

    public Task<string> CommitAsync(string checkoutPath, string message, GitIdentity author, CancellationToken ct)
    {
        var branch = _worktrees[Path.GetFullPath(checkoutPath)];
        var root = Path.GetFullPath(checkoutPath);
        var tree = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText);
        var current = _commits[_branches[branch]].Tree;
        if (tree.Count == current.Count && tree.All(kv => current.TryGetValue(kv.Key, out var t) && t == kv.Value))
        {
            throw new InvalidOperationException("nothing to commit");
        }
        return Task.FromResult(Add(branch, message, tree));
    }

    public Task<string> HeadAsync(string checkoutPath, CancellationToken ct) =>
        Task.FromResult(checkoutPath == Repo ? MainHead : _branches[_worktrees[Path.GetFullPath(checkoutPath)]]);

    public Task<IReadOnlyList<string>> PruneWorktreesAsync(string repoPath, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, string commitSha, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([.. _commits[commitSha].Tree.Keys.Order(StringComparer.Ordinal)]);

    public Task<string> ReadTextAsync(string repoPath, string commitSha, string path, CancellationToken ct) =>
        Task.FromResult(_commits[commitSha].Tree[path]);

    public Task<IReadOnlyList<GitCommitInfo>> ListCommitsAsync(string repoPath, string? excludeSha, string includeSha, CancellationToken ct)
    {
        var result = new List<GitCommitInfo>();
        var reachable = new List<string>();
        for (var sha = includeSha; ;)
        {
            reachable.Add(sha);
            var parents = _commits[sha].Parents;
            if (parents.Length == 0) break;
            sha = parents[0];
        }
        reachable.Reverse();
        foreach (var sha in reachable)
        {
            var c = _commits[sha];
            var before = c.Parents.Length > 0 ? _commits[c.Parents[0]].Tree : [];
            var touched = c.Tree.Keys.Union(before.Keys)
                .Where(k => !c.Tree.TryGetValue(k, out var a) || !before.TryGetValue(k, out var b) || a != b)
                .Order(StringComparer.Ordinal).ToList();
            result.Add(new GitCommitInfo(sha, c.Parents, "tester", DateTimeOffset.UnixEpoch, c.Message, touched));
        }
        return Task.FromResult<IReadOnlyList<GitCommitInfo>>(result);
    }

    public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<MergeResult> MergeAsync(string checkoutPath, string branch, GitIdentity author, CancellationToken ct, string? message = null) => throw new NotSupportedException();

    public Task AbortMergeAsync(string checkoutPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(string repoPath, string oldSha, string newSha, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> ResolveCommitAsync(string repoPath, string commitish, CancellationToken ct) => throw new NotSupportedException();

    public Task<RecoveryReport> RecoverAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeVaultWriter(FakeGit git) : IVaultWriter
{
    public List<VaultEditRequest> Requests { get; } = [];

    public int FailTimes { get; set; }

    public VaultEditOutcome? ForcedOutcome { get; set; }

    public Task<VaultEditResult> SubmitAsync(VaultEditRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (FailTimes > 0)
        {
            FailTimes--;
            throw new IOException("vault lane unavailable");
        }
        var old = git.MainHead;
        if (ForcedOutcome is { } forced)
        {
            return Task.FromResult(new VaultEditResult(forced, old, old, ["imports/x.md"], null));
        }
        var merged = git.FastForwardMain(request.Branch);
        return Task.FromResult(new VaultEditResult(merged ? VaultEditOutcome.Merged : VaultEditOutcome.UpToDate, old, git.MainHead, [], null));
    }

    public Task<VaultEditResult> CreateDecisionAsync(DecisionDraft draft, CancellationToken ct) => throw new NotSupportedException();
}
