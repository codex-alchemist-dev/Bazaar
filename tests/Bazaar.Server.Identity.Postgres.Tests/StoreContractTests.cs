using Bazaar.Server.Identity.Leases;
using Bazaar.Server.Identity.Postgres;

namespace Bazaar.Server.Identity.Postgres.Tests;

/// <summary>
/// Tests that need no database. Every other test in this project skips on a host without PostgreSQL (hosted Windows),
/// and a test project in which every test skips reports "zero tests ran" and fails the run; these keep it non-empty.
/// </summary>
public sealed class StoreContractTests
{
    [Fact]
    public void The_postgres_lease_store_implements_the_lease_store_contract() =>
        Assert.True(typeof(ICredentialLeaseStore).IsAssignableFrom(typeof(PostgresCredentialLeaseStore)));

    [Fact]
    public void The_postgres_lease_store_is_not_abstract() =>
        Assert.False(typeof(PostgresCredentialLeaseStore).IsAbstract);
}
