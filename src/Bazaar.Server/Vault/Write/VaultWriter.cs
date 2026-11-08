using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Vault.Write;

public enum VaultEditOutcome
{
    /// <summary>The agent's change is on the Vault's main branch.</summary>
    Merged,

    /// <summary>Nothing to record: the content and the branch already match main.</summary>
    NoChange,

    /// <summary>The merge conflicted. Main is unchanged, and the agent branch is kept for a rebase and resubmit.</summary>
    Conflicted,
}

/// <param name="RepoPath">The Vault's main checkout.</param>
/// <param name="Branch">The agent's branch, for example <c>agent/task-42</c>. Created from HEAD when it does not exist.</param>
/// <param name="Path">Target document path, relative and ending in <c>.md</c>. Set this or <see cref="DecisionSlug"/>, not both.</param>
/// <param name="DecisionSlug">When set, the writer assigns the next free <c>decisions/NNNN-&lt;slug&gt;.md</c> path.</param>
/// <param name="Content">The complete new file content, LF line endings.</param>
/// <param name="BaseCommit">The commit the agent edited from. Recorded as <c>Bazaar-Edit-Base</c>.</param>
public sealed record VaultEditRequest(
    string RepoPath,
    string Branch,
    string? Path,
    string? DecisionSlug,
    string Content,
    string AgentName,
    string AgentEmail,
    string BaseCommit,
    Guid? GcId,
    Guid? TaskId);

/// <param name="Outcome">What happened.</param>
/// <param name="CommitSha">The main-branch commit after the edit; null when conflicted.</param>
/// <param name="Path">The path written, including an assigned decision path.</param>
/// <param name="DecisionNumber">The assigned <c>NNNN</c> for a decision; null otherwise.</param>
/// <param name="ConflictPaths">Conflicting paths when <see cref="Outcome"/> is Conflicted.</param>
public sealed record VaultEditResult(
    VaultEditOutcome Outcome,
    string? CommitSha,
    string? Path,
    int? DecisionNumber,
    IReadOnlyList<string> ConflictPaths);

public interface IVaultWriter
{
    /// <summary>Records one agent edit on main, one at a time per Vault (doc 18, Write path).</summary>
    Task<VaultEditResult> SubmitAsync(VaultEditRequest request, CancellationToken ct);
}

/// <summary>
/// The single write path into a Vault repository (doc 18, "Write path"). An edit is committed on the agent's branch in a
/// temporary worktree, then merged into main. Submissions run one at a time per repository, so decision numbers stay
/// unique and merges never interleave. A conflicted merge leaves main untouched and keeps the branch.
/// </summary>
/// <remarks>
/// Not in this version: conflict cards to the GC, memory batching and the Move command (doc 18 WP8). The queue is per
/// repository, not per document: stricter than the design, which allows parallel documents.
/// </remarks>
public sealed partial class VaultWriter(IGitProvider git, GitIdentity? committer = null) : IVaultWriter
{
    private static readonly GitIdentity _defaultCommitter = new("Bazaar Vault Writer", "vault-writer@bazaar.invalid");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _queues = new(StringComparer.Ordinal);
    private readonly GitIdentity _committer = committer ?? _defaultCommitter;

    [GeneratedRegex(@"^decisions/(\d{4})-[^/]+\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionPathPattern();

    public async Task<VaultEditResult> SubmitAsync(VaultEditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseCommit);
        if ((request.Path is null) == (request.DecisionSlug is null))
        {
            throw new ArgumentException("Set exactly one of Path or DecisionSlug.", nameof(request));
        }

        if (request.Path is not null)
        {
            ValidateDocumentPath(request.Path);
        }

        var queue = _queues.GetOrAdd(Path.GetFullPath(request.RepoPath), _ => new SemaphoreSlim(1, 1));
        await queue.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await WriteAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            queue.Release();
        }
    }

    private async Task<VaultEditResult> WriteAsync(VaultEditRequest request, CancellationToken ct)
    {
        var head = await git.ResolveCommitAsync(request.RepoPath, "HEAD", ct).ConfigureAwait(false);

        int? decisionNumber = null;
        var path = request.Path ?? string.Empty;
        if (request.DecisionSlug is not null)
        {
            ValidateSlug(request.DecisionSlug);
            decisionNumber = NextDecisionNumber(await git.ListFilesAsync(request.RepoPath, head, ct).ConfigureAwait(false));
            path = $"decisions/{decisionNumber:D4}-{request.DecisionSlug}.md";
        }

        var worktree = WorktreePath(request.RepoPath, request.Branch);
        await ResetWorktreeAsync(request.RepoPath, worktree, ct).ConfigureAwait(false);
        await git.AddWorktreeAsync(request.RepoPath, request.Branch, worktree, ct).ConfigureAwait(false);
        try
        {
            var target = System.IO.Path.Combine(worktree, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            var changed = !File.Exists(target) || File.ReadAllText(target) != request.Content;
            File.WriteAllText(target, request.Content, new UTF8Encoding(false));

            string? agentCommit = null;
            if (changed)
            {
                agentCommit = await git.CommitAsync(
                    worktree,
                    CommitMessage(path, request),
                    new GitIdentity(request.AgentName, request.AgentEmail),
                    ct).ConfigureAwait(false);
            }

            var merge = await git.MergeAsync(request.RepoPath, request.Branch, _committer, ct).ConfigureAwait(false);
            if (merge.Outcome == GitMergeOutcome.Conflicted)
            {
                await git.AbortMergeAsync(request.RepoPath, ct).ConfigureAwait(false);
                return new VaultEditResult(VaultEditOutcome.Conflicted, null, path, decisionNumber, merge.ConflictPaths);
            }

            var outcome = agentCommit is null && merge.Outcome == GitMergeOutcome.UpToDate
                ? VaultEditOutcome.NoChange
                : VaultEditOutcome.Merged;
            return new VaultEditResult(outcome, merge.CommitSha ?? head, path, decisionNumber, []);
        }
        finally
        {
            await ResetWorktreeAsync(request.RepoPath, worktree, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Removes the worktree directory and its git data. The branch itself is kept.</summary>
    private async Task ResetWorktreeAsync(string repoPath, string worktree, CancellationToken ct)
    {
        if (Directory.Exists(worktree))
        {
            Directory.Delete(worktree, recursive: true);
        }

        await git.PruneWorktreesAsync(repoPath, ct).ConfigureAwait(false);
    }

    private static string CommitMessage(string path, VaultEditRequest request)
    {
        var message = new StringBuilder();
        message.Append("Edit ").Append(path).Append("\n\n");
        message.Append("Bazaar-Agent: ").Append(request.AgentName).Append('\n');
        if (request.GcId is { } gc)
        {
            message.Append("Bazaar-GC: ").Append(gc).Append('\n');
        }

        if (request.TaskId is { } task)
        {
            message.Append("Bazaar-Task: ").Append(task).Append('\n');
        }

        message.Append("Bazaar-Edit-Base: ").Append(request.BaseCommit).Append('\n');
        return message.ToString();
    }

    private static int NextDecisionNumber(IReadOnlyList<string> files)
    {
        var highest = 0;
        foreach (var file in files)
        {
            var match = DecisionPathPattern().Match(file);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
            {
                highest = Math.Max(highest, number);
            }
        }

        return highest + 1;
    }

    private static string WorktreePath(string repoPath, string branch)
    {
        var full = System.IO.Path.GetFullPath(repoPath).TrimEnd(System.IO.Path.DirectorySeparatorChar, '/');
        var parent = System.IO.Path.GetDirectoryName(full)!;
        var folder = System.IO.Path.GetFileName(full) + "-worktrees";
        Directory.CreateDirectory(System.IO.Path.Combine(parent, folder));
        return System.IO.Path.Combine(parent, folder, branch.Replace('/', '_'));
    }

    private static void ValidateDocumentPath(string path)
    {
        if (!path.EndsWith(".md", StringComparison.Ordinal)
            || path.StartsWith('/')
            || path.Contains('\\')
            || path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException($"'{path}' is not a relative Vault document path ending in .md.", nameof(path));
        }
    }

    private static void ValidateSlug(string slug)
    {
        if (slug.Length == 0 || !slug.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))
        {
            throw new ArgumentException("A decision slug uses ASCII letters, digits and hyphens only.", nameof(slug));
        }
    }
}
