namespace Bazaar.Server.Setup;

/// <summary>
/// Keeps the outcome of each apply, keyed by plan hash, so a repeat apply of the same hash returns it instead of applying again.
/// The first saved result for a hash is the one kept. The durable store is <c>PostgresApplyResultStore</c> in Bazaar.Server.Setup.Postgres; tests use an in-memory one.
/// </summary>
public interface IApplyResultStore
{
    Task<SetupApplyResult?> TryGetAsync(string planHash, CancellationToken cancellationToken = default);

    Task SaveAsync(string planHash, SetupApplyResult result, CancellationToken cancellationToken = default);
}
