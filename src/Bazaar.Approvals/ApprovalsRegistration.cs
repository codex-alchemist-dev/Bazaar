using Bazaar.Approvals.Db;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Approvals;

public static class ApprovalsRegistration
{
    /// <summary>
    /// Registers the owner-signature contract (doc 32 section 5a) on its durable stores: the key registry over <c>approval_key</c>,
    /// the single-use nonce store over <c>owner_nonce</c>, and the verifier every owner-gated action shares. The host must register
    /// an <c>NpgsqlDataSource</c> first. Every registration is TryAdd, so a host or a test can substitute a store before this call.
    /// </summary>
    public static IServiceCollection AddBazaarApprovals(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOwnerKeyRegistry, PostgresOwnerKeyRegistry>();
        services.TryAddSingleton<IOwnerNonceStore, PostgresOwnerNonceStore>();
        services.TryAddSingleton<IOwnerSignatureVerifier, OwnerSignatureVerifier>();
        return services;
    }
}
