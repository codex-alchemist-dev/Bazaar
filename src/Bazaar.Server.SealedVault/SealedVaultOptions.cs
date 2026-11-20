using Bazaar.Server.SealedGit;

namespace Bazaar.Server.SealedVault;

/// <summary>Settings of the sealed Vault (doc 61 "Vault adoption"). Paths are host facts, not registry settings.</summary>
/// <param name="Name">The sealed repo name (scope <c>vault:&lt;Name&gt;</c>), for example the company id.</param>
/// <param name="MountRoot">
/// The only folder the plaintext mount may use: tmpfs or the encrypted volume of doc 63 (owner ruling 2026-11-20, DECIDED). Owner-only, with no
/// link in any component.
/// </param>
/// <param name="RepoPath">
/// The Vault's main checkout (<c>Memory:Vault:RepoPath</c>). The mount is made at exactly this path, because the Vault writer and the memory
/// vault write files into the checkout and the worktrees by path; so it must be inside <paramref name="MountRoot"/>.
/// </param>
/// <param name="WorktreeRoot">Where batch worktrees go (<c>Memory:Vault:WorktreeRoot</c>); must be inside <paramref name="MountRoot"/> and outside the checkout.</param>
/// <param name="ImportFrom">
/// An existing plaintext checkout to import once (verify, then wipe) before the first mount, outside <paramref name="MountRoot"/>. Null when
/// none. When the folder no longer exists the import is done and this is ignored.
/// </param>
/// <param name="AdoptStaleMount">Operator-approved recovery of a stale mount with unsealed refs (see <see cref="SealedGitMountRequest.AdoptStaleMount"/>). Default false.</param>
public sealed record SealedVaultOptions(string Name, string MountRoot, string RepoPath, string WorktreeRoot, string? ImportFrom = null, bool AdoptStaleMount = false)
{
    /// <summary>Checks the paths and returns a copy with canonical paths (links resolved). Throws <see cref="InvalidOperationException"/> naming the problem.</summary>
    public SealedVaultOptions Normalised()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(MountRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(RepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorktreeRoot);
        if (SealedPaths.HasLinkComponent(MountRoot))
        {
            throw new InvalidOperationException("The sealed Vault mount root must not be or sit below a link.");
        }

        var root = SealedPaths.Canonicalize(MountRoot);
        var repo = SealedPaths.Canonicalize(RepoPath);
        var worktrees = SealedPaths.Canonicalize(WorktreeRoot);
        var import = string.IsNullOrWhiteSpace(ImportFrom) ? null : SealedPaths.Canonicalize(ImportFrom);

        if (!SealedPaths.IsStrictlyInside(repo, root))
        {
            throw new InvalidOperationException("The Vault repo path must be inside the sealed Vault mount root (after resolving links), so its plaintext checkout lives only there.");
        }

        if (!SealedPaths.IsStrictlyInside(worktrees, root) || SealedPaths.IsAtOrInside(worktrees, repo) || SealedPaths.IsStrictlyInside(repo, worktrees))
        {
            throw new InvalidOperationException("The Vault worktree root must be inside the sealed Vault mount root and outside the checkout.");
        }

        if (import is not null && (SealedPaths.IsAtOrInside(import, root) || SealedPaths.IsStrictlyInside(root, import)))
        {
            throw new InvalidOperationException("The plaintext import source must be outside the sealed Vault mount root.");
        }

        return this with { MountRoot = root, RepoPath = repo, WorktreeRoot = worktrees, ImportFrom = import };
    }
}
