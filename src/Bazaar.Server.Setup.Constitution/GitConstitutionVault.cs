using System.Text;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Index;
using LibGit2Sharp;

namespace Bazaar.Server.Setup.Constitution;

/// <summary>Brings the Vault index up to a commit. In production this is the Vault indexer.</summary>
public interface IVaultCatchUp
{
    /// <summary>Indexes the repository at <paramref name="headCommit"/> for <paramref name="vaultId"/>.</summary>
    Task<VaultIndexResult> CatchUpAsync(Guid vaultId, string repoPath, string headCommit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes the Constitution into the Vault repository as <c>company/Constitution.md</c>, commits it, and indexes it, so
/// <c>vault_docs</c> holds a <c>constitution</c> row whose head commit the owner can accept. It runs once, under the setup instance
/// lock, which is also the single writer of the checkout while setup runs. A Vault that is not a repository yet is initialised.
/// A Constitution already in the Vault is never overwritten.
/// </summary>
public sealed class GitConstitutionVault(ConstitutionVaultOptions options, IGitProvider git, IVaultCatchUp catchUp) : IConstitutionVault
{
    private const string _commitMessage = "Seed the default company Constitution\n\nBazaar-Setup: constitution.seed\n";

    /// <inheritdoc />
    public async Task<ConstitutionSeedResult> EnsureAsync(string markdown, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);

        var repo = Path.GetFullPath(options.RepoPath);
        Directory.CreateDirectory(repo);
        if (!Repository.IsValid(repo)) Repository.Init(repo);

        var head = await TryResolveHeadAsync(repo, cancellationToken);
        var exists = head is not null
            && (await git.ListFilesAsync(repo, head, cancellationToken)).Contains(IConstitutionVault.Path, StringComparer.Ordinal);

        var created = false;
        if (!exists)
        {
            var file = Path.Combine(repo, IConstitutionVault.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // A file that is in the working tree but not yet committed is the owner's draft: commit it as it is.
            if (!File.Exists(file)) await File.WriteAllTextAsync(file, markdown, new UTF8Encoding(false), cancellationToken);
            head = await git.CommitAsync(repo, _commitMessage, new GitIdentity(options.WriterName, options.WriterEmail), cancellationToken);
            created = true;
        }

        var indexed = await catchUp.CatchUpAsync(options.VaultId, repo, head!, cancellationToken);
        if (indexed.InvalidPaths.Contains(IConstitutionVault.Path, StringComparer.Ordinal))
        {
            throw new ConstitutionVaultException(
                "The Constitution file in the Vault has invalid front matter, so it is not indexed. Fix the file, then apply again.");
        }

        return new ConstitutionSeedResult(head!, created);
    }

    private async Task<string?> TryResolveHeadAsync(string repo, CancellationToken cancellationToken)
    {
        try
        {
            return await git.ResolveCommitAsync(repo, "HEAD", cancellationToken);
        }
        catch (ArgumentException)
        {
            // An empty repository has no commit yet.
            return null;
        }
    }
}

/// <summary>Stands in when the Vault is not configured, so the step fails with a plain message instead of writing somewhere unplanned.</summary>
public sealed class UnconfiguredConstitutionVault : IConstitutionVault
{
    /// <inheritdoc />
    public Task<ConstitutionSeedResult> EnsureAsync(string markdown, CancellationToken cancellationToken = default) =>
        throw new ConstitutionVaultException(
            $"The Vault is not configured, so the Constitution cannot be seeded. Set {ConstitutionVaultOptions.RepoPathKey}, " +
            $"{ConstitutionVaultOptions.VaultIdKey}, {ConstitutionVaultOptions.WriterNameKey} and {ConstitutionVaultOptions.WriterEmailKey}.");
}

/// <summary><see cref="IVaultCatchUp"/> over the Vault indexer.</summary>
public sealed class VaultIndexerCatchUp(VaultIndexer indexer) : IVaultCatchUp
{
    /// <inheritdoc />
    public Task<VaultIndexResult> CatchUpAsync(Guid vaultId, string repoPath, string headCommit, CancellationToken cancellationToken = default) =>
        indexer.CatchUpAsync(vaultId, repoPath, headCommit, cancellationToken);
}
