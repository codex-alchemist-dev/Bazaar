namespace Bazaar.Server.SealedVault;

/// <summary>Settings of the sealed Vault (doc 61 "Vault adoption", PROPOSED). Paths are host facts, not registry settings.</summary>
/// <param name="Name">The sealed repo name (scope <c>vault:&lt;Name&gt;</c>), for example the company id.</param>
/// <param name="MountRoot">The only folder the plaintext mount may use: the encrypted volume of doc 63 or a tmpfs. Owner-only.</param>
/// <param name="RepoPath">
/// The Vault's main checkout (<c>Memory:Vault:RepoPath</c>). The mount is made at exactly this path, because the Vault writer and the memory
/// vault write files into the checkout and the worktrees by path; so it must be inside <paramref name="MountRoot"/>.
/// </param>
/// <param name="WorktreeRoot">Where batch worktrees go (<c>Memory:Vault:WorktreeRoot</c>); must be inside <paramref name="MountRoot"/> and outside the checkout.</param>
/// <param name="ImportFrom">
/// An existing plaintext checkout to import once (verify, then wipe) before the first mount, outside <paramref name="MountRoot"/>. Null when
/// none. When the folder no longer exists the import is done and this is ignored.
/// </param>
public sealed record SealedVaultOptions(string Name, string MountRoot, string RepoPath, string WorktreeRoot, string? ImportFrom = null)
{
    /// <summary>Checks the paths and returns a copy with full, trimmed paths. Throws <see cref="InvalidOperationException"/> naming the problem.</summary>
    public SealedVaultOptions Normalised()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(MountRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(RepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorktreeRoot);
        var root = Full(MountRoot);
        var repo = Full(RepoPath);
        var worktrees = Full(WorktreeRoot);
        var import = string.IsNullOrWhiteSpace(ImportFrom) ? null : Full(ImportFrom);

        if (!IsInside(repo, root))
        {
            throw new InvalidOperationException("The Vault repo path must be inside the sealed Vault mount root, so its plaintext checkout lives only there.");
        }

        if (!IsInside(worktrees, root) || IsInside(worktrees, repo) || IsInside(repo, worktrees) || PathEquals(worktrees, repo))
        {
            throw new InvalidOperationException("The Vault worktree root must be inside the sealed Vault mount root and outside the checkout.");
        }

        if (import is not null && (PathEquals(import, root) || IsInside(import, root) || IsInside(root, import)))
        {
            throw new InvalidOperationException("The plaintext import source must be outside the sealed Vault mount root.");
        }

        return new SealedVaultOptions(Name, root, repo, worktrees, import);
    }

    internal static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool PathEquals(string a, string b) => string.Equals(a, b, Comparison);

    /// <summary>True when the full path <paramref name="path"/> is strictly below the full path <paramref name="parent"/>.</summary>
    internal static bool IsInside(string path, string parent)
    {
        var prefix = parent + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, Comparison);
    }
}
