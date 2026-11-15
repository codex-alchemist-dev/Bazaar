using Bazaar.Components.ManagedPostgres;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Hosting;
using Bazaar.Server.Secrets;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

public sealed class ManagedDatabaseConnectionTests
{
    [Fact]
    public async Task A_managed_connection_string_replaces_the_secret_reference()
    {
        var provider = new FixedProvider("Host=127.0.0.1;Port=54320;Username=bazaar_admin;Password=x;Database=postgres");
        var resolver = new CountingResolver();
        await using var database = new DatabaseConnection(
            Options.Create(new BazaarHostOptions { DataRoot = Path.GetTempPath(), DatabaseConnectionReference = "env:DB" }),
            resolver,
            provider);

        var source = await database.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, resolver.Calls);
        Assert.Equal(54320, new NpgsqlConnectionStringBuilder(source.ConnectionString).Port);
    }

    [Fact]
    public async Task Without_a_managed_provider_the_secret_reference_is_used()
    {
        var resolver = new CountingResolver();
        await using var database = new DatabaseConnection(
            Options.Create(new BazaarHostOptions { DataRoot = Path.GetTempPath(), DatabaseConnectionReference = "env:DB" }),
            resolver);

        await database.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, resolver.Calls);
    }

    private sealed class FixedProvider(string connectionString) : IManagedPostgresConnectionProvider
    {
        public Task<string?> GetConnectionStringAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(connectionString);
    }

    private sealed class CountingResolver : ISecretResolver
    {
        public int Calls { get; private set; }

        public bool CanResolve(string reference) => true;

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult("Host=localhost;Port=5432;Username=u;Password=p;Database=d");
        }
    }
}
