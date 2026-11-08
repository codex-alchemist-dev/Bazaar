namespace Bazaar.Components.ManagedPostgres;

/// <summary>
/// Gives the host a connection string for the managed cluster, starting the cluster on first use. Returns null in External
/// mode, so the host falls back to its configured secret reference.
/// </summary>
public interface IManagedPostgresConnectionProvider
{
    Task<string?> GetConnectionStringAsync(CancellationToken cancellationToken);
}
