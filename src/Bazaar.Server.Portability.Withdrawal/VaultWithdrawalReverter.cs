using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Portability.Withdrawal;

/// <summary>
/// Vault side of a withdrawal (doc 44 section 8.8): one new commit that restores the prior state of each file the import wrote.
/// History stays. <see cref="IVaultWriter"/> only lands branches, so the revert commit is made on a short-lived
/// <c>agent/import-withdraw-*</c> branch in a worktree through <see cref="IGitProvider"/> (the same abstraction
/// <c>VaultImportTarget</c> uses) and landed through <see cref="IVaultWriter.SubmitAsync"/>, which serialises it with every
/// other Vault write and tells the indexer.
/// </summary>
/// <remarks>
/// What the import wrote is read from git: the import commits carry the trailer <c>Bazaar-Import-Job: &lt;job&gt;</c>, and each
/// note carries <c>imported_job</c> and <c>source_key</c> in its front matter. A path's prior state is its content in the first
/// parent of the import commit that first touched it: restored when it existed there, deleted when the import created it.
/// Idempotent: a note whose front matter no longer names the job has already been reverted and is skipped, and a branch left by
/// a failed run is reused.
/// </remarks>
public sealed class VaultWithdrawalReverter(
    IGitProvider git,
    IVaultWriter vaultWriter,
    string repoPath,
    string worktreeRoot,
    GitIdentity identity,
    string agentId = VaultWithdrawalReverter.DefaultAgentId) : IVaultWithdrawalReverter
{
    /// <summary>The agent id recorded in the merge trailers.</summary>
    public const string DefaultAgentId = "system:import-withdrawal";

    private const string _importTrailer = "Bazaar-Import-Job: ";

    /// <inheritdoc />
    public async Task RevertAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(sourceKeys);
        _ = VaultImportTarget.PathFor(jobId, "x"); // the job id must be a safe path and branch segment
        if (sourceKeys.Count == 0)
        {
            return;
        }

        var head = await git.HeadAsync(repoPath, ct);
        var paths = await ResolvePathsAsync(jobId, sourceKeys, head, ct);
        if (paths.Count == 0)
        {
            return; // everything was reverted by an earlier run
        }

        var commits = await git.ListCommitsAsync(repoPath, null, head, ct);
        var restores = new List<(string Path, string? Text)>();
        var treeCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            var importCommit = commits.FirstOrDefault(c => IsImportCommit(c, jobId) && c.TouchedPaths.Contains(path, StringComparer.Ordinal))
                ?? throw new InvalidOperationException("No import commit found for Vault path " + path);
            string? prior = null;
            if (importCommit.Parents.Count > 0)
            {
                var parent = importCommit.Parents[0];
                if (!treeCache.TryGetValue(parent, out var files))
                {
                    files = new HashSet<string>(await git.ListFilesAsync(repoPath, parent, ct), StringComparer.Ordinal);
                    treeCache[parent] = files;
                }
                if (files.Contains(path))
                {
                    prior = await git.ReadTextAsync(repoPath, parent, path, ct);
                }
            }
            restores.Add((path, prior));
        }

        var name = "import-withdraw-" + jobId + "-" + batchIndex.ToString(CultureInfo.InvariantCulture);
        var branch = "agent/" + name;
        var worktree = Path.Combine(worktreeRoot, name);
        try
        {
            if (Directory.Exists(worktree))
            {
                Directory.Delete(worktree, recursive: true);
                await git.PruneWorktreesAsync(repoPath, ct);
            }
            await git.AddWorktreeAsync(repoPath, branch, worktree, ct);

            var changed = false;
            foreach (var (path, text) in restores)
            {
                var full = Path.GetFullPath(Path.Combine(worktree, path));
                if (!full.StartsWith(Path.GetFullPath(worktree) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Vault path escapes the worktree: " + path);
                }
                if (text is null)
                {
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                        changed = true;
                    }
                }
                else if (!File.Exists(full) || !string.Equals(await File.ReadAllTextAsync(full, ct), text, StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await File.WriteAllTextAsync(full, text, new UTF8Encoding(false), ct);
                    changed = true;
                }
            }

            // A branch left by a failed run already holds the revert commit, so there is nothing new to commit.
            if (changed)
            {
                var message = "withdraw import " + jobId + " batch " + batchIndex.ToString(CultureInfo.InvariantCulture)
                    + "\n\nBazaar-Import-Withdraw-Job: " + jobId
                    + "\nBazaar-Import-Withdraw-Batch: " + batchIndex.ToString(CultureInfo.InvariantCulture);
                await git.CommitAsync(worktree, message, identity, ct);
            }

            var request = new VaultEditRequest(DocIdFor(jobId, batchIndex), head, branch, agentId, null, null);
            var result = await vaultWriter.SubmitAsync(request, ct);
            if (result.Outcome is VaultEditOutcome.Conflicted or VaultEditOutcome.Refused)
            {
                throw new InvalidOperationException("Vault refused the withdrawal of batch " + batchIndex.ToString(CultureInfo.InvariantCulture)
                    + " (" + result.Outcome + "): " + string.Join(", ", result.ConflictPaths));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(worktree))
                {
                    Directory.Delete(worktree, recursive: true);
                }
                await git.PruneWorktreesAsync(repoPath, CancellationToken.None);
            }
            catch (IOException)
            {
                // A leftover worktree directory is removed by the next run for this batch.
            }
        }
    }

    /// <summary>A stable document id for the per-batch merge queue key.</summary>
    public static Guid DocIdFor(string jobId, int batchIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("import-withdraw\0" + jobId + "\0" + batchIndex.ToString(CultureInfo.InvariantCulture)));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static bool IsImportCommit(GitCommitInfo commit, string jobId) =>
        commit.Message.Split('\n').Any(line => string.Equals(line.TrimEnd('\r'), _importTrailer + jobId, StringComparison.Ordinal));

    /// <summary>
    /// The current path of each key's note at <paramref name="head"/>. The default import path is checked first; only keys not
    /// found there (a note a replace wrote in place) cause a scan of the other notes. A key with no note is already reverted.
    /// </summary>
    private async Task<HashSet<string>> ResolvePathsAsync(string jobId, IReadOnlyList<string> sourceKeys, string head, CancellationToken ct)
    {
        var files = await git.ListFilesAsync(repoPath, head, ct);
        var fileSet = new HashSet<string>(files, StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new HashSet<string>(sourceKeys, StringComparer.Ordinal);
        foreach (var key in sourceKeys)
        {
            var path = VaultImportTarget.PathFor(jobId, key);
            if (fileSet.Contains(path) && await BelongsToJobAsync(path, jobId, key, head, ct))
            {
                resolved.Add(path);
                remaining.Remove(key);
            }
        }

        if (remaining.Count == 0)
        {
            return resolved;
        }

        var skip = "imports/" + jobId + "/";
        foreach (var path in files.Where(p => p.EndsWith(".md", StringComparison.Ordinal) && !resolved.Contains(p)))
        {
            ct.ThrowIfCancellationRequested();
            if (path.StartsWith(skip, StringComparison.Ordinal))
            {
                continue; // default-path notes were handled above
            }
            var text = await git.ReadTextAsync(repoPath, head, path, ct);
            if (string.Equals(VaultMarkdown.ReadFront(text, "imported_job"), jobId, StringComparison.Ordinal)
                && VaultMarkdown.ReadSourceKey(text) is { } key && remaining.Remove(key))
            {
                resolved.Add(path);
            }
        }
        return resolved;
    }

    private async Task<bool> BelongsToJobAsync(string path, string jobId, string key, string head, CancellationToken ct)
    {
        var text = await git.ReadTextAsync(repoPath, head, path, ct);
        return string.Equals(VaultMarkdown.ReadFront(text, "imported_job"), jobId, StringComparison.Ordinal)
            && string.Equals(VaultMarkdown.ReadSourceKey(text), key, StringComparison.Ordinal);
    }
}
