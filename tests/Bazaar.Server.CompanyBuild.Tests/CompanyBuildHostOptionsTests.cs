using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.Server.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class CompanyBuildHostOptionsTests
{
    private static readonly string Pin = new('b', 64);

    private static ServiceCollection Register(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        new CompanyBuildComponent().AddServices(services, new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services;
    }

    [Fact]
    public async Task A_complete_section_makes_the_identity_check_evaluate()
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-cb-" + Guid.NewGuid().ToString("N"));
        using var provider = Register(new()
        {
            ["Bazaar:CompanyBuild:CompanyId"] = "CDAX",
            ["Bazaar:CompanyBuild:ServerAddress"] = "https://bazaar.example/",
            ["Bazaar:CompanyBuild:ServerKeyHashPin"] = Pin,
            ["Bazaar:CompanyBuild:OutputRoot"] = root,
        }).BuildServiceProvider();

        var check = Assert.IsType<ServerIdentityCheck>(Assert.Single(provider.GetServices<ISetupVerificationCheck>()));
        Assert.Equal(Path.Combine(root, "CDAX"), provider.GetRequiredService<CompanyBuildOutputLayout>().CompanyDirectory);
        Assert.Equal(Pin, provider.GetRequiredService<IServerIdentitySource>().KeyHashPin);

        // No build has run yet, so the check is amber (no-build), not green and not "not evaluated".
        var result = await check.RunAsync(new SetupVerificationContext(new SetupPlan()), TestContext.Current.CancellationToken);
        Assert.Equal(SetupCheckStatus.Amber, result.Status);
    }

    [Fact]
    public void The_output_root_defaults_under_the_data_root()
    {
        using var provider = Register(new()
        {
            ["Bazaar:CompanyBuild:CompanyId"] = "CDAX",
            ["Bazaar:CompanyBuild:ServerAddress"] = "https://bazaar.example/",
            ["Bazaar:CompanyBuild:ServerKeyHashPin"] = Pin,
            ["Bazaar:Host:DataRoot"] = Path.GetTempPath(),
        }).BuildServiceProvider();

        Assert.Equal(
            Path.Combine(Path.GetFullPath(Path.GetTempPath()), "company-build", "CDAX"),
            provider.GetRequiredService<CompanyBuildOutputLayout>().CompanyDirectory);
    }

    [Fact]
    public void An_empty_section_registers_nothing() =>
        Assert.DoesNotContain(Register([]), d => d.ServiceType == typeof(IServerIdentitySource));

    [Theory]
    [InlineData("CompanyId", "CDAX")]
    [InlineData("ServerAddress", "https://bazaar.example/")]
    [InlineData("ServerKeyHashPin", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void A_partial_section_is_a_configuration_error(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => Register(new() { [$"Bazaar:CompanyBuild:{key}"] = value }));

    [Theory]
    [InlineData("not-a-url", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("https://bazaar.example/", "short")]
    public void A_malformed_value_stops_startup(string address, string pin) =>
        Assert.Throws<InvalidOperationException>(() => Register(new()
        {
            ["Bazaar:CompanyBuild:CompanyId"] = "CDAX",
            ["Bazaar:CompanyBuild:ServerAddress"] = address,
            ["Bazaar:CompanyBuild:ServerKeyHashPin"] = pin,
            ["Bazaar:CompanyBuild:OutputRoot"] = Path.GetTempPath(),
        }));
}
