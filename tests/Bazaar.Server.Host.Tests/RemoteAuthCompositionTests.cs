using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Auth;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Http.Auth;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Identity.Postgres;
using Bazaar.Server.Identity.Signing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

/// <summary>The access-token signer wiring: which signer the host registers for each environment and whether a key store and KEK exist (doc 32 section 5).</summary>
public sealed class RemoteAuthCompositionTests
{
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Bazaar.Server.Host.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(bool devSeed = false)
    {
        var values = new Dictionary<string, string?> { [RemoteAuthCompositionExtensions.DevIssuerEnabledKey] = devSeed ? "true" : "false" };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>Builds a container like the host does: the KEK, the clock and optionally a database data source, then the remote auth.</summary>
    private static ServiceProvider Build(string environment, bool withKek, bool withDatabase)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment(environment));
        services.AddSingleton(TimeProvider.System);
        if (withKek) services.AddSingleton<IKekProvider>(new TestKek());
        if (withDatabase) services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none"));
        services.AddBazaarRemoteAuth(Config());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void In_development_with_a_kek_the_production_signer_is_registered_and_round_trips()
    {
        using var provider = Build("Development", withKek: true, withDatabase: false);
        var issuer = provider.GetRequiredService<IAccessTokenIssuer>();
        var verifier = provider.GetRequiredService<IAccessTokenVerifier>();
        var deviceId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        var token = issuer.Issue(deviceId, humanId, DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.IsType<SignerAccessTokenIssuer>(issuer);
        Assert.True(verifier.TryVerify(token, out var caller));
        Assert.Equal(new AuthenticatedCaller(deviceId, humanId), caller);
    }

    [Fact]
    public void With_a_database_the_store_is_the_postgres_store_in_any_environment()
    {
        using var provider = Build("Production", withKek: true, withDatabase: true);

        Assert.IsType<PostgresSigningKeyStore>(provider.GetRequiredService<ISigningKeyStore>());
        Assert.IsType<SignerAccessTokenIssuer>(provider.GetRequiredService<IAccessTokenIssuer>());
    }

    [Fact]
    public void In_production_without_a_database_nothing_is_signed()
    {
        using var provider = Build("Production", withKek: true, withDatabase: false);
        var issuer = provider.GetRequiredService<IAccessTokenIssuer>();

        var ex = Assert.Throws<AccessTokenIssuerUnavailableException>(() => issuer.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.DoesNotContain("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_a_kek_nothing_is_signed_even_in_development()
    {
        using var provider = Build("Development", withKek: false, withDatabase: false);
        var verifier = provider.GetRequiredService<IAccessTokenVerifier>();

        Assert.Throws<AccessTokenIssuerUnavailableException>(() => provider.GetRequiredService<IAccessTokenIssuer>().Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.False(verifier.TryVerify("v1.00000000000000000000000000000000.e30.AAAA", out _));
    }

    [Fact]
    public void A_token_from_one_host_does_not_verify_on_another_host()
    {
        using var first = Build("Development", withKek: true, withDatabase: false);
        using var second = Build("Development", withKek: true, withDatabase: false);
        var token = first.GetRequiredService<IAccessTokenIssuer>().Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.False(second.GetRequiredService<IAccessTokenVerifier>().TryVerify(token, out _));
    }

    [Fact]
    public void The_signing_settings_come_from_configuration_with_documented_defaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment("Development"));
        services.AddSingleton(TimeProvider.System);
        services.AddBazaarRemoteAuth(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Remote:Signing:SigningKeyRotationDays"] = "7",
        }).Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<AccessTokenSigningOptions>();

        Assert.Equal(7, options.SigningKeyRotationDays);
        Assert.Equal(TimeSpan.FromMinutes(15), options.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ClockSkew);
    }
}
