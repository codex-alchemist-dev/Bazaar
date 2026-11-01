using Bazaar.Server.Host.Composition;
using Bazaar.Server.Secrets;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests;

public sealed class DevAuthTests
{
    private static readonly DeviceInfo _device = new("device-1", "desktop", "0.1.0");

    private static ServiceProvider Build(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBazaarSecrets();
        services.AddBazaarDevAuth(configuration);
        services.AddBazaarHostWire(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string EnvName() => "BAZAAR_DEV_AUTH_TEST_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Accepts_the_configured_token_and_returns_the_owner_principal()
    {
        var env = EnvName();
        Environment.SetEnvironmentVariable(env, "dev-secret-123");
        try
        {
            await using var sp = Build(new()
            {
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + env,
            });
            var auth = sp.GetRequiredService<IConnectionAuthenticator>();
            Assert.IsType<DevTokenAuthenticator>(auth);

            var result = await auth.AuthenticateAsync(new HelloCredential("dev-secret-123"), _device, TestContext.Current.CancellationToken);

            Assert.True(result.Ok);
            Assert.NotNull(result.Principal);
            Assert.Equal("owner", result.Principal.Kind);
            Assert.Equal(DevTokenAuthenticator.ToPrincipalGuid("owner"), result.Principal.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    [Fact]
    public async Task Accepts_the_token_with_a_bearer_prefix()
    {
        var env = EnvName();
        Environment.SetEnvironmentVariable(env, "dev-secret-123");
        try
        {
            await using var sp = Build(new()
            {
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + env,
            });
            var auth = sp.GetRequiredService<IConnectionAuthenticator>();

            var result = await auth.AuthenticateAsync(new HelloCredential("Bearer dev-secret-123"), _device, TestContext.Current.CancellationToken);

            Assert.True(result.Ok);
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    [Fact]
    public async Task Uses_a_configured_principal_id_when_it_is_a_guid()
    {
        var env = EnvName();
        var principal = Guid.NewGuid();
        Environment.SetEnvironmentVariable(env, "dev-secret-123");
        try
        {
            await using var sp = Build(new()
            {
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + env,
                ["Bazaar:DevAuth:PrincipalId"] = principal.ToString(),
            });

            var result = await sp.GetRequiredService<IConnectionAuthenticator>()
                .AuthenticateAsync(new HelloCredential("dev-secret-123"), _device, TestContext.Current.CancellationToken);

            Assert.True(result.Ok);
            Assert.Equal(principal, result.Principal!.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("dev-secret-12")]
    [InlineData("dev-secret-1234")]
    [InlineData("Bearer wrong-secret")]
    [InlineData("Bearer ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Refuses_wrong_empty_and_missing_credentials(string? presented)
    {
        var env = EnvName();
        Environment.SetEnvironmentVariable(env, "dev-secret-123");
        try
        {
            await using var sp = Build(new()
            {
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + env,
            });
            var credential = presented is null ? null : new HelloCredential(presented);

            var result = await sp.GetRequiredService<IConnectionAuthenticator>()
                .AuthenticateAsync(credential, _device, TestContext.Current.CancellationToken);

            Assert.False(result.Ok);
            Assert.Null(result.Principal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    [Fact]
    public async Task Denies_all_when_dev_auth_is_disabled()
    {
        await using var sp = Build(new()
        {
            ["Bazaar:DevAuth:Enabled"] = "false",
            ["Bazaar:DevAuth:TokenSecret"] = "env:UNUSED_BAZAAR_DEV_AUTH_TOKEN",
        });
        var auth = sp.GetRequiredService<IConnectionAuthenticator>();

        Assert.IsType<DenyAllAuthenticator>(auth);
        var result = await auth.AuthenticateAsync(new HelloCredential("anything"), _device, TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Denies_all_when_dev_auth_is_not_configured()
    {
        await using var sp = Build([]);

        Assert.IsType<DenyAllAuthenticator>(sp.GetRequiredService<IConnectionAuthenticator>());
    }

    [Fact]
    public async Task Start_fails_with_a_clear_error_when_enabled_without_a_secret()
    {
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:DevAuth:Enabled"] = "true",
            }))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddBazaarSecrets();
                services.AddBazaarDevAuth(context.Configuration);
                services.AddBazaarHostWire(context.Configuration);
            })
            .Build();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Bazaar:DevAuth:TokenSecret", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_fails_when_the_secret_reference_does_not_resolve()
    {
        var env = EnvName();
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + env,
            }))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddBazaarSecrets();
                services.AddBazaarDevAuth(context.Configuration);
                services.AddBazaarHostWire(context.Configuration);
            })
            .Build();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(env, ex.ToString(), StringComparison.Ordinal);
    }
}
