using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Vault.Index;

/// <summary>Connects the writer to the indexer: every landed commit becomes a catch-up to that commit.</summary>
public sealed class VaultIndexCommitObserver(VaultIndexer indexer, Guid vaultId, string repoPath) : IVaultCommitObserver
{
    public async Task OnCommitAsync(string oldCommit, string newCommit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(newCommit);
        await indexer.CatchUpAsync(vaultId, repoPath, newCommit, ct).ConfigureAwait(false);
    }
}
