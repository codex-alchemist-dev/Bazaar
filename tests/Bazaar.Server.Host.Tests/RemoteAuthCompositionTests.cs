using System.Text;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Auth;
using Bazaar.Server.Host.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests;

/// <summary>The dev-only access-token issuer, its gate, and the token it signs (doc 32 section 5; the dev credential is not production).</summary>
public sealed class RemoteAuthCompositionTests
{
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Bazaar.Server.Host.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(bool? devEnabled)
    {
        var values = new Dictionary<string, string?>();
        if (devEnabled is { } enabled) values[RemoteAuthCompositionExtensions.DevIssuerEnabledKey] = enabled ? "true" : "false";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IAccessTokenIssuer ResolveIssuer(string environment, bool? devEnabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment(environment));
        services.AddSingleton(TimeProvider.System);
        services.AddBazaarRemoteAuth(Config(devEnabled));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAccessTokenIssuer>();
    }

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Development", null, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    public void The_dev_issuer_is_registered_only_when_the_flag_is_on_and_the_environment_is_development(string environment, bool? devEnabled, bool expectDev)
    {
        var issuer = ResolveIssuer(environment, devEnabled);

        Assert.Equal(expectDev, issuer is DevAccessTokenIssuer);
        Assert.Equal(!expectDev, issuer is UnavailableAccessTokenIssuer);
    }

    [Fact]
    public void Without_a_dev_issuer_issuing_a_token_throws_unavailable_and_names_no_secret()
    {
        var issuer = ResolveIssuer("Production", devEnabled: false);

        var ex = Assert.Throws<AccessTokenIssuerUnavailableException>(() => issuer.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.DoesNotContain("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_dev_token_verifies_under_its_own_key_and_expires_in_the_future()
    {
        var issuer = new DevAccessTokenIssuer(TimeProvider.System);
        var deviceId = Guid.NewGuid();
        var humanId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);

        var token = issuer.Issue(deviceId, humanId, expiresAt);

        Assert.True(issuer.TryVerify(token, out var claims));
        Assert.Equal(deviceId, claims.DeviceId);
        Assert.Equal(humanId, claims.HumanId);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), claims.Exp);
        Assert.True(claims.Exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public void A_token_from_one_process_instance_does_not_verify_with_another_instances_key()
    {
        var first = new DevAccessTokenIssuer(TimeProvider.System);
        var second = new DevAccessTokenIssuer(TimeProvider.System);

        var token = first.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.False(second.TryVerify(token, out _));
    }

    [Fact]
    public void A_token_with_a_forged_payload_does_not_verify()
    {
        var issuer = new DevAccessTokenIssuer(TimeProvider.System);
        var token = issuer.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(15));
        var mac = token[(token.IndexOf('.') + 1)..];
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"00000000-0000-0000-0000-000000000000\",\"hum\":\"00000000-0000-0000-0000-000000000000\",\"exp\":4102444800}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(issuer.TryVerify(forged + "." + mac, out _));
    }

    [Fact]
    public void An_expired_dev_token_does_not_verify()
    {
        var issuer = new DevAccessTokenIssuer(TimeProvider.System);
        var token = issuer.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.False(issuer.TryVerify(token, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-dot-here")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    public void Malformed_tokens_do_not_verify(string token)
    {
        var issuer = new DevAccessTokenIssuer(TimeProvider.System);

        Assert.False(issuer.TryVerify(token, out _));
    }
}
