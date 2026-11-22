using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Athanor;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Athanor.Vault;

/// <summary>Settings of the Vault workspace (<c>Memory:Vault:*</c>, doc 18 and doc 57 section 6).</summary>
/// <param name="WorkspaceId">The workspace id clients send, <c>vault</c> by default.</param>
/// <param name="RepoPath">The Vault's main checkout (<c>Memory:Vault:RepoPath</c>).</param>
/// <param name="WorktreeRoot">Where edit worktrees are made.</param>
/// <param name="Author">The identity that commits an Athanor save. The editor's wire request carries no person, so the audit row (user, path, revisions) names who saved.</param>
public sealed record VaultEditorOptions(string WorkspaceId, string RepoPath, string WorktreeRoot, GitIdentity Author)
{
    /// <summary>The default workspace id.</summary>
    public const string DefaultWorkspaceId = "vault";

    /// <summary>The <c>Bazaar-Agent</c> trailer value of an Athanor save.</summary>
    public const string EditorAgentId = "athanor";
}

/// <summary>
/// <see cref="IEditorWorkspaceStore"/> that saves Vault files through the Vault commit path (doc 57 section 6, PROPOSED; doc 18 write
/// path). Reads, path containment and the symlink rules come from the inner directory store. A save is a compare-and-set against the
/// file's current revision, then an <c>athanor/edit-*</c> branch in its own worktree holds one commit, and <see cref="IVaultWriter"/>
/// merges it under the Vault lane (trailers, frozen-decision check, indexer notification). Writes to other workspaces pass through.
/// </summary>
public sealed class VaultEditorWorkspaceStore : IEditorWorkspaceStore
{
    private readonly IEditorWorkspaceStore _inner;
    private readonly IGitProvider _git;
    private readonly IVaultWriter _writer;
    private readonly IAthanorLimits _limits;
    private readonly VaultEditorOptions _options;

    public VaultEditorWorkspaceStore(IEditorWorkspaceStore inner, IGitProvider git, IVaultWriter writer, IAthanorLimits limits, VaultEditorOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _git = git;
        _writer = writer;
        _limits = limits;
        _options = options;
    }

    public ValueTask<EditorWorkspaceInfo?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
        _inner.GetWorkspaceAsync(workspaceId, cancellationToken);

    public ValueTask<EditorReadResult> ReadAsync(string workspaceId, string path, long maxBytes, CancellationToken cancellationToken) =>
        _inner.ReadAsync(workspaceId, path, maxBytes, cancellationToken);

    public async ValueTask<EditorWriteResult> WriteAsync(string workspaceId, string path, string baseRevision, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!string.Equals(workspaceId, _options.WorkspaceId, StringComparison.Ordinal))
        {
            return await _inner.WriteAsync(workspaceId, path, baseRevision, content, cancellationToken).ConfigureAwait(false);
        }

        var workspace = await _inner.GetWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null || workspace.ReadOnly)
        {
            return new EditorWriteResult(EditorWriteStatus.Blocked);
        }

        var current = await _inner.ReadAsync(workspaceId, path, _limits.MaxOpenBytes, cancellationToken).ConfigureAwait(false);
        switch (current.Status)
        {
            case EditorReadStatus.NotFound:
                return new EditorWriteResult(EditorWriteStatus.NotFound);
            case EditorReadStatus.Blocked:
                return new EditorWriteResult(EditorWriteStatus.Blocked);
            case EditorReadStatus.TooLarge:
                return new EditorWriteResult(EditorWriteStatus.TooLarge);
        }

        if (!string.Equals(current.Revision, baseRevision, StringComparison.Ordinal))
        {
            return new EditorWriteResult(EditorWriteStatus.Conflict, ServerRevision: current.Revision, ServerContent: current.Content);
        }

        var newRevision = DirectoryEditorWorkspaceStore.RevisionOf(content);
        if (string.Equals(newRevision, current.Revision, StringComparison.Ordinal))
        {
            return new EditorWriteResult(EditorWriteStatus.Written, NewRevision: newRevision);
        }

        return await CommitAsync(workspaceId, path, content, newRevision, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EditorWriteResult> CommitAsync(string workspaceId, string path, byte[] content, string newRevision, CancellationToken cancellationToken)
    {
        var branch = "athanor/edit-" + Guid.NewGuid().ToString("N");
        var merged = false;
        var worktree = Path.Combine(_options.WorktreeRoot, branch.Replace('/', '-'));
        try
        {
            var baseCommit = await _git.HeadAsync(_options.RepoPath, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(_options.WorktreeRoot);
            await _git.AddWorktreeAsync(_options.RepoPath, branch, worktree, cancellationToken).ConfigureAwait(false);
            var target = Path.Combine(worktree, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target))
            {
                // The file is not in the committed tree (new, ignored or untracked): the Vault path only lands tracked content.
                return new EditorWriteResult(EditorWriteStatus.NotFound);
            }

            await File.WriteAllBytesAsync(target, content, cancellationToken).ConfigureAwait(false);
            await _git.CommitAsync(worktree, "Edit " + path + " in Athanor", _options.Author, cancellationToken).ConfigureAwait(false);

            var result = await _writer.SubmitAsync(
                new VaultEditRequest(DocIdFor(path), baseCommit, branch, VaultEditorOptions.EditorAgentId, null, null),
                cancellationToken).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case VaultEditOutcome.Merged:
                case VaultEditOutcome.UpToDate:
                    merged = true;
                    return new EditorWriteResult(EditorWriteStatus.Written, NewRevision: newRevision);
                case VaultEditOutcome.Conflicted:
                    // Someone else landed a change to this file between the check and the merge: hand back the server copy.
                    var latest = await _inner.ReadAsync(workspaceId, path, _limits.MaxOpenBytes, cancellationToken).ConfigureAwait(false);
                    return latest.Status == EditorReadStatus.Ok
                        ? new EditorWriteResult(EditorWriteStatus.Conflict, ServerRevision: latest.Revision, ServerContent: latest.Content)
                        : throw new EditorStoreUnavailableException("The Vault changed while saving.");
                default:
                    return new EditorWriteResult(EditorWriteStatus.Blocked);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LibGit2Sharp.LibGit2SharpException or ArgumentException or InvalidOperationException)
        {
            throw new EditorStoreUnavailableException("The Vault could not take the save.", e);
        }
        finally
        {
            CleanUp(worktree);
            try
            {
                await _git.PruneWorktreesAsync(_options.RepoPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or LibGit2Sharp.LibGit2SharpException)
            {
                // A stale worktree is pruned by the next save or at startup recovery.
            }

            if (merged)
            {
                try
                {
                    // The edit is in the Vault's history now; its branch has done its job. An unmerged branch (a conflict) is kept.
                    await _git.DeleteMergedBranchAsync(_options.RepoPath, branch, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or LibGit2Sharp.LibGit2SharpException)
                {
                    // A leftover merged branch is harmless.
                }
            }
        }
    }

    // One queue key per file keeps two saves of the same file in order.
    private static Guid DocIdFor(string path) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("bazaar-athanor:" + path))[..16]);

    private static void CleanUp(string worktree)
    {
        try
        {
            if (Directory.Exists(worktree))
            {
                foreach (var file in Directory.EnumerateFiles(worktree, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(worktree, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A locked directory is pruned on a later save.
        }
    }
}
