using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class ManagedPostgresRegistrationTests
{
    [Fact]
    public void Default_mode_is_external()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddBazaarManagedPostgres(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ManagedPostgresOptions>>().Value;

        Assert.Equal(DatabaseMode.External, options.Mode);
    }

    [Fact]
    public void Mode_is_bound_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Database:Mode"] = "Managed" })
            .Build();
        var services = new ServiceCollection().AddBazaarManagedPostgres(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ManagedPostgresOptions>>().Value;

        Assert.Equal(DatabaseMode.Managed, options.Mode);
    }
}
