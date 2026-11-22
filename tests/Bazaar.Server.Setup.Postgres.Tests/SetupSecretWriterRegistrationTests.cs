using System.Text;
using Bazaar.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres.Tests;

public sealed class SetupSecretWriterRegistrationTests
{
    private const string _canary = "canary-secret-value-7731";

    private sealed class CapturingStore : ISecretStore
    {
        public string? Stored { get; private set; }

        public Task PutAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretValue value, CancellationToken ct)
        {
            Stored = Encoding.UTF8.GetString(value.Bytes);
            return Task.CompletedTask;
        }

        public Task<SecretValue> GetAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretPurpose purpose, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(SecretCaller caller, SecretScope scope, SecretName name, CancellationToken ct) => throw new NotSupportedException();

        public Task CryptoEraseAsync(SecretCaller caller, AgentId agent, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task With_a_secret_store_the_writer_stores_the_value()
    {
        var store = new CapturingStore();
        using var provider = new ServiceCollection()
            .AddSingleton<ISecretStore>(store)
            .AddBazaarSetupPostgres()
            .BuildServiceProvider();

        var writer = provider.GetRequiredService<ISetupSecretWriter>();
        await writer.WriteAsync("secret://bazaar/a/b", _canary, TestContext.Current.CancellationToken);

        Assert.True(writer.StoreAvailable);
        Assert.Equal(_canary, store.Stored);
    }

    [Fact]
    public async Task Without_a_secret_store_the_writer_fails_closed_without_the_value()
    {
        using var provider = new ServiceCollection().AddBazaarSetupPostgres().BuildServiceProvider();

        var writer = provider.GetRequiredService<ISetupSecretWriter>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.WriteAsync("secret://bazaar/a/b", _canary, TestContext.Current.CancellationToken));

        Assert.False(writer.StoreAvailable);
        Assert.DoesNotContain(_canary, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_install_id_provider_is_registered()
    {
        using var provider = new ServiceCollection()
            .AddSingleton(NpgsqlDataSource.Create("Host=localhost"))
            .AddBazaarSetupPostgres()
            .BuildServiceProvider();

        Assert.IsType<PostgresInstallIdProvider>(provider.GetRequiredService<IInstallIdProvider>());
    }
}
