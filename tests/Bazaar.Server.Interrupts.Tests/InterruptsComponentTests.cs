using Cdax.Core;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Setup.FirstRun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Interrupts.Tests;

public sealed class InterruptsComponentTests
{
    private sealed class NoOwner : IFirstRunOwner
    {
        public Task<Guid?> GetOwnerPrincipalIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
    }

    [Fact]
    public void Manifest_exposes_the_interrupt_feature_and_requires_gc_and_setup()
    {
        var manifest = new InterruptsComponent().Manifest;

        Assert.Equal("interrupts", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Equal(["gc", "setup"], manifest.DependsOn);
        Assert.Equal(["feature:interrupt-stack"], manifest.Provides);
    }

    [Fact]
    public async Task Service_reader_and_cipher_resolve_to_one_stack_over_the_codec()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGcBodyCodec>(new FakeBodyCodec());
        services.AddSingleton<IFirstRunOwner, NoOwner>();
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        services.AddSingleton(dataSource);
        new InterruptsComponent().AddServices(services, new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        var service = provider.GetRequiredService<IInterruptStackService>();
        var reader = provider.GetRequiredService<IInterruptStackReader>();

        Assert.IsType<InterruptStackService>(service);
        Assert.Same(service, reader);
        Assert.IsType<GcBodyInterruptReasonCipher>(provider.GetRequiredService<IInterruptReasonCipher>());
    }
}
