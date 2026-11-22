using System.Globalization;
using System.Text;
using Bazaar.Server.Twin.Prose;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Twin;

/// <summary>
/// Lands one Twin page in the Vault (doc 25 WP4, doc 18 write path) and implements the prose step's <see cref="ITwinPageWriter"/> port.
/// The file is committed on an <c>agent/twin-&lt;project&gt;-&lt;page&gt;-&lt;suffix&gt;</c> branch in a short-lived worktree, then merged through
/// <see cref="IVaultWriter.SubmitAsync"/> under the <c>bazaar-twin</c> identity. Same shape as the Archive's writer.
/// </summary>
/// <remarks>
/// A Twin page is regenerated in place: an existing page is overwritten. When the text is identical to the page at the main
/// checkout's head nothing is committed. A conflicted or refused merge throws, so the step reports a failure and never a false success.
/// </remarks>
public sealed class TwinPageWriter : ITwinPageWriter
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

    /// <inheritdoc />
    public async Task<TwinPageRef?> GetLatestAsync(string project, string page, CancellationToken ct)
    {
        var path = TwinPaths.PagePath(project, page);
        var file = Path.Combine(_repoPath, path);
        if (!File.Exists(file))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        return new TwinPageRef(path, ReadVersion(text) ?? 1);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A name is not a safe path segment, or the path does not match the project and page.</exception>
    /// <exception cref="InvalidOperationException">The merge was conflicted or refused.</exception>
    public async Task<TwinPageRef> WriteAsync(TwinPageWrite page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        var pagePath = TwinPaths.PagePath(page.Project, page.Page);
        if (!string.Equals(page.Path, pagePath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The page path does not match its project and page.", nameof(page));
        }

        var written = new TwinPageRef(pagePath, ReadVersion(page.Markdown) ?? 1);
        var head = await _git.HeadAsync(_repoPath, ct).ConfigureAwait(false);

        // Nothing to do when the main checkout already holds this exact page: no branch, no commit.
        var mainFile = Path.Combine(_repoPath, pagePath);
        if (File.Exists(mainFile) && await File.ReadAllTextAsync(mainFile, ct).ConfigureAwait(false) == page.Markdown)
        {
            return written;
        }

        // A short random suffix keeps a branch kept from an earlier conflicted write from colliding with this one.
        var branch = $"agent/twin-{page.Project}-{page.Page}-{Guid.NewGuid():N}"[..^24];
        Directory.CreateDirectory(_worktreesRoot);
        var worktree = Path.Combine(_worktreesRoot, branch.Replace('/', '-'));
        await _git.AddWorktreeAsync(_repoPath, branch, worktree, ct).ConfigureAwait(false);
        try
        {
            var fullPath = Path.Combine(worktree, pagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, page.Markdown, new UTF8Encoding(false), ct).ConfigureAwait(false);
            await _git.CommitAsync(worktree, $"Twin page {page.Page} for {page.Project}", TwinIdentity, ct).ConfigureAwait(false);

            var result = await _writer.SubmitAsync(
                new VaultEditRequest(TwinPageRenderer.DocId(page.Project, page.Page), head, branch, TwinAgentId, GcId: null, TaskId: null),
                ct).ConfigureAwait(false);
            if (result.Outcome is VaultEditOutcome.Conflicted or VaultEditOutcome.Refused)
            {
                throw new InvalidOperationException($"The Vault did not take the Twin page: {result.Outcome}.");
            }

            return written;
        }
        finally
        {
            // The branch is kept (git history). Only the directory and its administrative entry go.
            if (Directory.Exists(worktree))
            {
                Directory.Delete(worktree, recursive: true);
            }

            await _git.PruneWorktreesAsync(_repoPath, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // The step writes a "version: N" line in the front matter; the page's version is read back from there.
    private static int? ReadVersion(string markdown)
    {
        var lines = markdown.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---")
        {
            return null;
        }

        for (var i = 1; i < lines.Length && lines[i].TrimEnd('\r') != "---"; i++)
        {
            if (lines[i].StartsWith("version: ", StringComparison.Ordinal)
                && int.TryParse(lines[i].AsSpan("version: ".Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
        }

        return null;
    }
}
