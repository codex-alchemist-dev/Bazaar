using Bazaar.Approvals;
using Bazaar.Approvals.Db;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Security.Approvals;

/// <summary>The owner-signature contract is composed once and every owner-gated action resolves the same verifier (doc 32 section 5a).</summary>
public sealed class ApprovalsRegistrationTests
{
    private static ServiceCollection WithDataSource()
    {
        var services = new ServiceCollection();
        // Creating a data source does not open a connection, so the graph resolves without a database.
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=unused"));
        return services;
    }

    [Fact]
    public void The_verifier_resolves_on_the_durable_stores()
    {
        var services = WithDataSource();
        services.AddBazaarApprovals();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<OwnerSignatureVerifier>(provider.GetRequiredService<IOwnerSignatureVerifier>());
        Assert.IsType<PostgresOwnerKeyRegistry>(provider.GetRequiredService<IOwnerKeyRegistry>());
        Assert.IsType<PostgresOwnerNonceStore>(provider.GetRequiredService<IOwnerNonceStore>());
    }

    [Fact]
    public void Every_consumer_gets_the_same_verifier_instance()
    {
        var services = WithDataSource();
        services.AddBazaarApprovals();
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IOwnerSignatureVerifier>(), provider.GetRequiredService<IOwnerSignatureVerifier>());
    }

    [Fact]
    public void A_store_registered_first_is_not_replaced()
    {
        var services = WithDataSource();
        var nonces = new InMemoryOwnerNonceStore();
        services.AddSingleton<IOwnerNonceStore>(nonces);
        services.AddBazaarApprovals();
        services.AddBazaarApprovals();
        using var provider = services.BuildServiceProvider();

        Assert.Same(nonces, provider.GetRequiredService<IOwnerNonceStore>());
    }
}
