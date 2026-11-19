using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Memory.GitVault;

/// <summary>Where the memory vault reads and writes (docs/design/08-memory-system.md, section 11).</summary>
/// <param name="RepoPath">The Vault's main checkout, the same path the <c>VaultWriter</c> merges into.</param>
/// <param name="WorktreeRoot">A directory for short-lived batch worktrees. It must be outside <paramref name="RepoPath"/>.</param>
/// <param name="AuthorFor">The commit author for an agent id (the agent's App identity, doc 18). No default exists.</param>
public sealed record GitMemoryVaultOptions(string RepoPath, string WorktreeRoot, Func<string, GitIdentity> AuthorFor);
