using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Bazaar.Server.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class CompositionTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBazaarHostRuntime(configuration);
        services.AddBazaarSecrets();
        services.AddBazaarProviders(configuration);
        services.AddBazaarWire();
        services.AddBazaarAgentSafety();
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task Core_services_resolve()
    {
        await using var sp = Build();
        Assert.IsType<JsonWireCodec>(sp.GetRequiredService<IWireCodec>());
        Assert.NotNull(sp.GetRequiredService<ProviderCapabilityReport>());
        Assert.NotNull(sp.GetRequiredService<IInstanceLock>());
        Assert.Equal(3, sp.GetServices<IReadinessCheck>().Count());
    }

    [Fact]
    public async Task Secret_resolver_handles_file_references_on_every_os()
    {
        await using var sp = Build();
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "s3cret\n", TestContext.Current.CancellationToken);
            var resolver = sp.GetRequiredService<ISecretResolver>();
            Assert.Equal("s3cret", await resolver.ResolveAsync("file:" + path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Unknown_secret_scheme_names_the_reference()
    {
        await using var sp = Build();
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await sp.GetRequiredService<ISecretResolver>().ResolveAsync("vault:thing", TestContext.Current.CancellationToken));
        Assert.Contains("vault:thing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_service_host_that_is_unregistered_fails_with_no_fallback()
    {
        await using var sp = Build(new() { ["Bazaar:Providers:ServiceHost"] = "no_such_host" });
        Assert.Throws<ProviderSelectionException>(() => sp.GetRequiredService<ProviderCapabilityReport>());
    }
}
