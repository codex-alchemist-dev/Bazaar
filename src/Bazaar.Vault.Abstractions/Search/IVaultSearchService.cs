namespace Bazaar.Server.Vault.Search;

/// <summary>Hybrid Vault search (doc 18, "Search"), as the abstraction other modules (Memory) depend on.</summary>
public interface IVaultSearchService
{
    Task<VaultSearchResult> SearchAsync(VaultSearchQuery query, CancellationToken cancellationToken);
}
