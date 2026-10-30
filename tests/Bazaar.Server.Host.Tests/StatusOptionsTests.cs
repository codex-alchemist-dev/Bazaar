using System.Net;
using Bazaar.Server.Host.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public sealed class StatusOptionsTests
{
    private static BazaarStatusOptions Bind(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        return configuration.GetSection(BazaarStatusOptions.SectionName).Get<BazaarStatusOptions>() ?? new BazaarStatusOptions();
    }

    [Fact]
    public void Default_bind_is_loopback()
    {
        var options = Bind([]);
        Assert.Equal("loopback", options.Bind);
        Assert.False(options.OwnerApprovedWideBind);
        Assert.Equal(IPAddress.Loopback, options.Address);
        Assert.True(new BazaarStatusOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Wildcard_bind_is_refused_without_owner_approval()
    {
        var options = Bind(new() { ["Bazaar:Host:Status:Bind"] = "0.0.0.0" });
        Assert.False(new BazaarStatusOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Wildcard_bind_is_accepted_only_with_owner_approval_flag()
    {
        var options = Bind(new()
        {
            ["Bazaar:Host:Status:Bind"] = "0.0.0.0",
            ["Bazaar:Host:Status:OwnerApprovedWideBind"] = "true",
        });
        Assert.Equal(IPAddress.Any, options.Address);
        Assert.True(new BazaarStatusOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Bind_that_is_neither_loopback_nor_an_address_is_rejected()
    {
        var options = Bind(new() { ["Bazaar:Host:Status:Bind"] = "lan" });
        Assert.False(new BazaarStatusOptionsValidator().Validate(null, options).Succeeded);
        Assert.Throws<InvalidOperationException>(() => options.Address);
    }
}
