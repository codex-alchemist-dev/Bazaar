using System.Text;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Knowledge.Archive;

/// <summary>
/// Lands one validated chapter in the Vault (doc 25 WP6, doc 18 write path). The chapter file is committed on an
/// <c>agent/archive-&lt;project&gt;-&lt;slug&gt;</c> branch in a short-lived worktree, then merged through
/// <see cref="VaultWriter.SubmitAsync"/> under the <c>bazaar-archive</c> identity. Nothing touches the main checkout
/// except the writer's merge.
/// </summary>
/// <remarks>
/// Chapters are never overwritten: the chapter path must not exist at the base commit, so a regenerated chapter uses a
/// new slug and names its predecessor. A predecessor path must exist at the base commit.
/// </remarks>
public sealed class ArchiveVaultWriter
{
    /// <summary>The identity recorded in the <c>Bazaar-Agent</c> trailer and as the chapter commit's author.</summary>
    public const string ArchiveAgentId = "bazaar-archive";

    /// <summary>The author of chapter commits. The domain is a reserved placeholder: the operator's domain is never hardcoded.</summary>
    public static readonly GitIdentity ArchiveIdentity = new(ArchiveAgentId, "bazaar-archive@invalid");

    private readonly IGitProvider _git;
    private readonly VaultWriter _writer;
    private readonly string _repoPath;
    private readonly string _worktreesRoot;

    /// <param name="git">The git engine for the Vault.</param>
    /// <param name="writer">The Vault's single write path. Every merge goes through it.</param>
    /// <param name="repoPath">The Vault's main checkout.</param>
    /// <param name="worktreesRoot">A directory for the short-lived chapter worktree. Must exist or be creatable.</param>
    public ArchiveVaultWriter(IGitProvider git, VaultWriter writer, string repoPath, string worktreesRoot)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _repoPath = repoPath ?? throw new ArgumentNullException(nameof(repoPath));
        _worktreesRoot = worktreesRoot ?? throw new ArgumentNullException(nameof(worktreesRoot));
    }

    /// <summary>
    /// Writes a validated chapter at <c>archive/&lt;project&gt;/&lt;slug&gt;.md</c>, measured against <paramref name="baseCommit"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The chapter path already exists, or the predecessor is missing at the base commit.</exception>
    public async Task<VaultEditResult> WriteAsync(ValidatedArchiveChapter chapter, string baseCommit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommit);

        var chapterPath = ArchivePaths.ChapterPath(chapter.Project, chapter.Slug);
        var branch = $"agent/archive-{chapter.Project}-{chapter.Slug}";
        var head = await _git.ResolveCommitAsync(_repoPath, baseCommit, ct);

        var files = await _git.ListFilesAsync(_repoPath, head, ct);
        if (files.Contains(chapterPath, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Chapter '{chapterPath}' already exists; regenerate it under a new slug.");
        }

        if (chapter.PredecessorPath is not null && !files.Contains(chapter.PredecessorPath, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Predecessor '{chapter.PredecessorPath}' does not exist at the base commit.");
        }

        Directory.CreateDirectory(_worktreesRoot);
        var worktree = Path.Combine(_worktreesRoot, branch.Replace('/', '-'));
        await _git.AddWorktreeAsync(_repoPath, branch, worktree, ct);
        try
        {
            var fullPath = Path.Combine(worktree, chapterPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, ArchiveChapterRenderer.Render(chapter), new UTF8Encoding(false), ct);
            await _git.CommitAsync(worktree, $"Archive chapter {chapter.Slug} for {chapter.Project}", ArchiveIdentity, ct);

            return await _writer.SubmitAsync(
                new VaultEditRequest(chapter.DocId, head, branch, ArchiveAgentId, GcId: null, TaskId: null),
                ct);
        }
        finally
        {
            // The branch is kept (git history). Only the directory and its administrative entry go.
            if (Directory.Exists(worktree))
            {
                Directory.Delete(worktree, recursive: true);
            }

            await _git.PruneWorktreesAsync(_repoPath, CancellationToken.None);
        }
    }
}
