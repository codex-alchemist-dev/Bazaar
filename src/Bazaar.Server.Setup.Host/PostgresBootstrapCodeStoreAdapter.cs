using Bazaar.Server.Setup.Http;
using Bazaar.Server.Setup.Postgres;

namespace Bazaar.Server.Setup.Host;

/// <summary>Presents the durable <see cref="PostgresBootstrapCodeStore"/> as the provisioning API's <see cref="IBootstrapCodeStore"/>.</summary>
public sealed class PostgresBootstrapCodeStoreAdapter(PostgresBootstrapCodeStore store) : IBootstrapCodeStore
{
    /// <inheritdoc />
    public Task<string> CreateAsync(CancellationToken cancellationToken = default) => store.CreateAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> ConsumeAsync(string presentedCode, CancellationToken cancellationToken = default) =>
        store.ConsumeAsync(presentedCode, cancellationToken);

    /// <inheritdoc />
    public Task RevokeAsync(CancellationToken cancellationToken = default) => store.RevokeAsync(cancellationToken);
}
