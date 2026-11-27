using Bazaar.Server.SealedGit;
using Bazaar.Server.SealedStorage;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.SealedVault;

/// <summary>
/// <see cref="IGitProvider"/> over a sealed Vault repo (doc 61 "Vault adoption", PROPOSED). Decorates the engine provider (LibGit2 by default):
/// calls on the Vault checkout or its worktrees run against the mounted plaintext copy, and every call that changes the git dir is followed by a
/// reseal. Calls on any other repository (for example a GitHub workspace) pass through untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paths.</b> The mount is made at the configured repo path itself, inside the mount root, because the Vault writer and the memory vault
/// write files into the checkout and their worktrees by path. So the "logical to mounted" map is the identity, and the provider's job is to
/// refuse any Vault path outside the mount: a worktree of the Vault outside the worktree root, or an unknown path inside the mount root.
/// </para>
/// <para>
/// <b>Writes are serialised.</b> A mutating call and its reseal run under one gate per Vault, so a reseal never sees a ref without the
/// objects it points to. This drops the engine's parallelism between a worktree commit and a merge into the main checkout.
/// </para>
/// <para>
/// <b>Fail closed.</b> A failed mount, import or reseal throws, is audited, and (for a reseal) leaves the provider faulted: every later Vault
/// call throws until the server restarts and remounts from the sealed state. A commit whose reseal failed is lost on that remount.
/// </para>
/// </remarks>
public sealed class SealedGitProvider : IGitProvider, ISealedVault, IAsyncDisposable, IDisposable
{
    private readonly IGitProvider _inner;
    private readonly ISealedGitRepo _repo;
    private readonly SealedVaultOptions _options;
    private readonly ISealAuditSink _audit;
    private readonly IMountRootVerifier _verifier;
    private readonly SemaphoreSlim _mountGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ISealedGitMount? _mount;
    private volatile bool _disposed;

    /// <param name="verifier">Confirms the mount root is tmpfs or the encrypted volume; the mount is refused otherwise (owner ruling 2026-11-20).</param>
    public SealedGitProvider(IGitProvider inner, ISealedGitRepo repo, SealedVaultOptions options, ISealAuditSink audit, IMountRootVerifier verifier)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Normalised();
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    private enum Target
    {
        Outside,
        Checkout,
        Worktree,
    }

    public string Scope => _repo.Scope;

    public string RepoPath => _options.RepoPath;

    public bool IsMounted => _mount is not null;

    public bool IsFaulted => _mount?.IsFaulted ?? false;

    public async Task EnsureMountedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _mount) is not null)
        {
            return;
        }

        await _mountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mount is not null)
            {
                return;
            }

            var verdict = _verifier.Verify(_options.MountRoot);
            if (!verdict.IsProtected)
            {
                await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealvault.mount_unverified", Scope, null, $"Mount root {_options.MountRoot} refused: {verdict.Reason}"), CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException($"The sealed Vault mount root is not confirmed as tmpfs or the encrypted volume ({verdict.Reason}).");
            }

            if (_options.ImportFrom is { } source && Directory.Exists(source))
            {
                await _repo.ImportCheckoutAsync(source, _options.MountRoot, _verifier, cancellationToken).ConfigureAwait(false);
            }

            var request = new SealedGitMountRequest(_options.MountRoot, _options.RepoPath, _verifier, _options.WorktreeRoot, _options.AdoptStaleMount);
            var mount = await _repo.MountAsync(request, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _mount, mount);
        }
        finally
        {
            _mountGate.Release();
        }
    }

    /// <summary>Synchronous dispose for hosts that dispose the container synchronously; wipes the mount like <see cref="DisposeAsync"/>.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _mountGate.WaitAsync().ConfigureAwait(false);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_mount is { } mount)
            {
                await mount.DisposeAsync().ConfigureAwait(false);
                _mount = null;
            }
        }
        finally
        {
            _writeGate.Release();
            _mountGate.Release();
        }
    }

    public async Task<string> AddWorktreeAsync(string repoPath, string branch, string worktreePath, CancellationToken ct)
    {
        var repoTarget = Classify(repoPath);
        var worktreeTarget = Classify(worktreePath);
        if (repoTarget == Target.Outside && worktreeTarget == Target.Outside)
        {
            return await _inner.AddWorktreeAsync(repoPath, branch, worktreePath, ct).ConfigureAwait(false);
        }

        if (repoTarget != Target.Checkout || worktreeTarget != Target.Worktree)
        {
            throw await RefuseAsync($"A worktree of the sealed Vault must be inside {_options.WorktreeRoot}, and nothing else may put a worktree there.").ConfigureAwait(false);
        }

        return await WriteAsync(() => _inner.AddWorktreeAsync(repoPath, branch, worktreePath, ct), ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string repoPath, CancellationToken ct) =>
        ReadAsync(repoPath, () => _inner.ListWorktreesAsync(repoPath, ct), ct);

    public Task<IReadOnlyList<string>> PruneWorktreesAsync(string repoPath, CancellationToken ct) =>
        MutateAsync(repoPath, () => _inner.PruneWorktreesAsync(repoPath, ct), ct);

    public Task<string> CommitAsync(string checkoutPath, string message, GitIdentity author, CancellationToken ct) =>
        MutateAsync(checkoutPath, () => _inner.CommitAsync(checkoutPath, message, author, ct), ct);

    public Task<MergeResult> MergeAsync(string checkoutPath, string branch, GitIdentity author, CancellationToken ct, string? message = null) =>
        MutateAsync(checkoutPath, () => _inner.MergeAsync(checkoutPath, branch, author, ct, message), ct);

    public Task<string> HeadAsync(string checkoutPath, CancellationToken ct) =>
        ReadAsync(checkoutPath, () => _inner.HeadAsync(checkoutPath, ct), ct);

    public Task AbortMergeAsync(string checkoutPath, CancellationToken ct) =>
        MutateAsync(checkoutPath, async () =>
        {
            await _inner.AbortMergeAsync(checkoutPath, ct).ConfigureAwait(false);
            return true;
        }, ct);

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(string repoPath, string oldSha, string newSha, CancellationToken ct) =>
        ReadAsync(repoPath, () => _inner.DiffAsync(repoPath, oldSha, newSha, ct), ct);

    public Task<string> ResolveCommitAsync(string repoPath, string commitish, CancellationToken ct) =>
        ReadAsync(repoPath, () => _inner.ResolveCommitAsync(repoPath, commitish, ct), ct);

    public Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, string commitSha, CancellationToken ct) =>
        ReadAsync(repoPath, () => _inner.ListFilesAsync(repoPath, commitSha, ct), ct);

    public Task<string> ReadTextAsync(string repoPath, string commitSha, string path, CancellationToken ct) =>
        ReadAsync(repoPath, () => _inner.ReadTextAsync(repoPath, commitSha, path, ct), ct);

    public Task<IReadOnlyList<GitCommitInfo>> ListCommitsAsync(string repoPath, string? excludeSha, string includeSha, CancellationToken ct) =>
        ReadAsync(repoPath, () => _inner.ListCommitsAsync(repoPath, excludeSha, includeSha, ct), ct);

    public Task<RecoveryReport> RecoverAsync(string repoPath, CancellationToken ct) =>
        MutateAsync(repoPath, () => _inner.RecoverAsync(repoPath, ct), ct);

    private async Task<T> ReadAsync<T>(string path, Func<Task<T>> call, CancellationToken ct)
    {
        if (Classify(path) == Target.Outside)
        {
            return await call().ConfigureAwait(false);
        }

        await EnsureUsableAsync(ct).ConfigureAwait(false);
        return await call().ConfigureAwait(false);
    }

    private async Task<T> MutateAsync<T>(string path, Func<Task<T>> call, CancellationToken ct)
    {
        if (Classify(path) == Target.Outside)
        {
            return await call().ConfigureAwait(false);
        }

        return await WriteAsync(call, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a mutating call on the mount and reseals after it, also when it throws (the git dir may have changed part way).</summary>
    private async Task<T> WriteAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        await EnsureUsableAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var mount = await EnsureUsableAsync(ct).ConfigureAwait(false);
            T result;
            try
            {
                result = await call().ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await mount.ResealAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The mount is faulted and the failure audited; the caller sees the original error.
                }

                throw;
            }

            await mount.ResealAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<ISealedGitMount> EnsureUsableAsync(CancellationToken ct)
    {
        await EnsureMountedAsync(ct).ConfigureAwait(false);
        var mount = Volatile.Read(ref _mount) ?? throw new ObjectDisposedException(nameof(SealedGitProvider));
        if (mount.IsFaulted)
        {
            throw new InvalidOperationException($"The sealed Vault {Scope} is faulted after a failed reseal; restart the server to remount from the sealed state.");
        }

        return mount;
    }

    /// <summary>Which part of the sealed Vault a path names. Paths inside the mount root that are neither are refused.</summary>
    private Target Classify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Canonical (links resolved), so an alias of the checkout is still the checkout and cannot slip past the seal.
        var full = SealedPaths.Canonicalize(path);
        if (SealedPaths.IsAtOrInside(full, _options.RepoPath))
        {
            return Target.Checkout;
        }

        if (SealedPaths.IsStrictlyInside(full, _options.WorktreeRoot))
        {
            return Target.Worktree;
        }

        if (SealedPaths.IsAtOrInside(full, _options.MountRoot))
        {
            SealedVaultAudit.Record(_audit, "sealvault.refused", Scope, $"Git call on {full} inside the mount root refused: not the Vault checkout or a worktree.");
            throw new InvalidOperationException("That path is inside the sealed Vault mount root but is not the Vault checkout or one of its worktrees.");
        }

        return Target.Outside;
    }

    private async Task<Exception> RefuseAsync(string reason)
    {
        await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealvault.refused", Scope, null, reason), CancellationToken.None).ConfigureAwait(false);
        return new InvalidOperationException(reason);
    }
}

/// <summary>Audit helper: an audit failure must not hide the refusal it reports.</summary>
internal static class SealedVaultAudit
{
    public static void Record(ISealAuditSink audit, string kind, string scope, string detail)
    {
        try
        {
            audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, kind, scope, null, detail), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Swallowed on purpose; the caller throws its own error.
        }
    }
}
