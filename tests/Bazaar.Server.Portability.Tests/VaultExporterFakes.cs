using System.Text;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Portability.Tests;

/// <summary>A fixed Vault at one commit. Only the read calls the exporter uses are implemented.</summary>
internal sealed class FakeVaultGit(string headSha, IReadOnlyDictionary<string, string> files) : IGitProvider
{
    public List<string> ReadPaths { get; } = [];

    public Task<string> ResolveCommitAsync(string repoPath, string commitish, CancellationToken ct)
    {
        if (commitish is "HEAD" or "main") return Task.FromResult(headSha);
        throw new InvalidOperationException("Unknown commit-ish: " + commitish);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, string commitSha, CancellationToken ct)
    {
        IReadOnlyList<string> paths = files.Keys.ToList();
        return Task.FromResult(paths);
    }

    public Task<string> ReadTextAsync(string repoPath, string commitSha, string path, CancellationToken ct)
    {
        ReadPaths.Add(path);
        return Task.FromResult(files[path]);
    }

    public Task<string> AddWorktreeAsync(string repoPath, string branch, string worktreePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> PruneWorktreesAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> CommitAsync(string checkoutPath, string message, GitIdentity author, CancellationToken ct) => throw new NotSupportedException();

    public Task<MergeResult> MergeAsync(string checkoutPath, string branch, GitIdentity author, CancellationToken ct, string? message = null) => throw new NotSupportedException();

    public Task<string> HeadAsync(string checkoutPath, CancellationToken ct) => throw new NotSupportedException();

    public Task AbortMergeAsync(string checkoutPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(string repoPath, string oldSha, string newSha, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<GitCommitInfo>> ListCommitsAsync(string repoPath, string? excludeSha, string includeSha, CancellationToken ct) => throw new NotSupportedException();

    public Task<RecoveryReport> RecoverAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A bundle writer with fixed output, so the bundle bytes are known in tests.</summary>
internal sealed class FakeBundleWriter(byte[] output) : IVaultBundleWriter
{
    public List<string> RepoPaths { get; } = [];

    public async Task WriteBundleAsync(string repoPath, Stream destination, CancellationToken ct)
    {
        RepoPaths.Add(repoPath);
        await destination.WriteAsync(output, ct);
    }
}

/// <summary>Keeps every file in memory. Refuses to replace a file, as the directory sink does.</summary>
internal sealed class MemoryVaultExportSink : IVaultExportSink
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public List<string> OpenedPaths { get; } = [];

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public Task<Stream> OpenWriteAsync(string relativePath, CancellationToken ct)
    {
        OpenedPaths.Add(relativePath);
        if (_files.ContainsKey(relativePath)) throw new IOException("File already exists: " + relativePath);
        Stream stream = new CommitOnDisposeStream(bytes => _files[relativePath] = bytes);
        return Task.FromResult(stream);
    }

    public string Text(string path) => Encoding.UTF8.GetString(_files[path]);

    /// <summary>Collects the written bytes and hands them to the sink when the exporter disposes the stream.</summary>
    private sealed class CommitOnDisposeStream(Action<byte[]> commit) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) commit(ToArray());
            base.Dispose(disposing);
        }
    }
}
