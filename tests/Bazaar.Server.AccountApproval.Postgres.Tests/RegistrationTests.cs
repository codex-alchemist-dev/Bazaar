using Bazaar.Server.AccountApproval.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.AccountApproval.Postgres.Tests;

/// <summary>The durable store must win over the in-memory default whichever is registered first. No database is needed.</summary>
public sealed class RegistrationTests
{
    [Fact]
    public void Postgres_store_replaces_an_earlier_in_memory_default()
    {
        var services = new ServiceCollection();
        services.TryAddSingleton<IAccountApprovalStore, InMemoryAccountApprovalStore>();

        services.AddBazaarAccountApprovalPostgres();

        Assert.Equal(typeof(PostgresAccountApprovalStore), Assert.Single(services, d => d.ServiceType == typeof(IAccountApprovalStore)).ImplementationType);
    }

    [Fact]
    public void A_later_in_memory_default_does_not_replace_the_postgres_store()
    {
        var services = new ServiceCollection();
        services.AddBazaarAccountApprovalPostgres();

        services.TryAddSingleton<IAccountApprovalStore, InMemoryAccountApprovalStore>();

        Assert.Equal(typeof(PostgresAccountApprovalStore), Assert.Single(services, d => d.ServiceType == typeof(IAccountApprovalStore)).ImplementationType);
    }
}
