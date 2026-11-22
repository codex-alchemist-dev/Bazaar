using System.Text;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Twin;

/// <summary>
/// Lands one validated Twin page in the Vault (doc 25 WP4, doc 18 write path). The page file is committed on an
/// <c>agent/twin-&lt;project&gt;-&lt;page&gt;-&lt;suffix&gt;</c> branch in a short-lived worktree, then merged through
/// <see cref="IVaultWriter.SubmitAsync"/> under the <c>bazaar-twin</c> identity. Same shape as the Archive's writer.
/// </summary>
/// <remarks>
/// Unlike an Archive chapter, a Twin page is regenerated in place: an existing page is overwritten. When the rendered text is
/// identical to the page at the base commit nothing is committed and the result is <see cref="VaultEditOutcome.UpToDate"/>.
/// A page with no accepted sentence is never written.
/// </remarks>
public sealed class TwinPageWriter
{
    /// <summary>The identity recorded in the <c>Bazaar-Agent</c> trailer and as the page commit's author.</summary>
    public const string TwinAgentId = "bazaar-twin";

    /// <summary>The author of page commits. The domain is a reserved placeholder: the operator's domain is never hardcoded.</summary>
    public static readonly GitIdentity TwinIdentity = new(TwinAgentId, "bazaar-twin@invalid");

    private readonly IGitProvider _git;
    private readonly IVaultWriter _writer;
    private readonly string _repoPath;
    private readonly string _worktreesRoot;

    /// <param name="git">The git engine for the Vault.</param>
    /// <param name="writer">The Vault's single write path. Every merge goes through it.</param>
    /// <param name="repoPath">The Vault's main checkout.</param>
    /// <param name="worktreesRoot">A directory for the short-lived page worktree. Must exist or be creatable.</param>
    public TwinPageWriter(IGitProvider git, IVaultWriter writer, string repoPath, string worktreesRoot)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _repoPath = repoPath ?? throw new ArgumentNullException(nameof(repoPath));
        _worktreesRoot = worktreesRoot ?? throw new ArgumentNullException(nameof(worktreesRoot));
    }

    /// <summary>Writes a validated page at <c>twin/&lt;project&gt;/&lt;page&gt;.md</c>, measured against <paramref name="baseCommit"/>.</summary>
    /// <exception cref="ArgumentException">A project or page name is not a safe path segment.</exception>
    /// <exception cref="InvalidOperationException">The page has no accepted sentence.</exception>
    public async Task<VaultEditResult> WriteAsync(ValidatedTwinPage page, string baseCommit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommit);
        var pagePath = TwinPaths.PagePath(page.Project, page.Page);
        if (page.Sentences.Count == 0)
        {
            throw new InvalidOperationException("A page with no accepted sentence is never written.");
        }

        var head = await _git.ResolveCommitAsync(_repoPath, baseCommit, ct);
        var content = TwinPageRenderer.Render(page);

        // Nothing to do when the main checkout is at the base commit and already holds this exact page: no branch, no commit.
        var mainFile = Path.Combine(_repoPath, pagePath);
        if (await _git.HeadAsync(_repoPath, ct) == head && File.Exists(mainFile) && await File.ReadAllTextAsync(mainFile, ct) == content)
        {
            return new VaultEditResult(VaultEditOutcome.UpToDate, head, head, [], null);
        }

        // A short random suffix keeps a branch kept from an earlier conflicted write from colliding with this one.
        var branch = $"agent/twin-{page.Project}-{page.Page}-{Guid.NewGuid():N}"[..^24];
        Directory.CreateDirectory(_worktreesRoot);
        var worktree = Path.Combine(_worktreesRoot, branch.Replace('/', '-'));
        await _git.AddWorktreeAsync(_repoPath, branch, worktree, ct);
        try
        {
            var fullPath = Path.Combine(worktree, pagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(false), ct);
            await _git.CommitAsync(worktree, $"Twin page {page.Page} for {page.Project}", TwinIdentity, ct);

            return await _writer.SubmitAsync(
                new VaultEditRequest(TwinPageRenderer.DocId(page.Project, page.Page), head, branch, TwinAgentId, GcId: null, TaskId: null),
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
