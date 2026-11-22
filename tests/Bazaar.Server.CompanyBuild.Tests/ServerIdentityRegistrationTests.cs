using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.Server.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class ServerIdentityRegistrationTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        new CompanyBuildComponent().AddServices(services, new ConfigurationBuilder().Build());
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Without_the_host_inputs_the_check_is_not_evaluated()
    {
        using var provider = Build();
        var check = Assert.Single(provider.GetServices<ISetupVerificationCheck>());

        Assert.Equal("server.identity", check.Id);
        var result = await check.RunAsync(new SetupVerificationContext(new SetupPlan()), TestContext.Current.CancellationToken);
        Assert.Equal(SetupCheckStatus.NotEvaluated, result.Status);
    }

    [Fact]
    public void With_the_host_inputs_the_real_check_is_registered()
    {
        using var provider = Build(s =>
        {
            s.AddSingleton(new CompanyBuildOutputLayout(Path.GetTempPath(), "CDAX"));
            s.AddSingleton<IServerIdentitySource>(new StaticServerIdentitySource(new Uri("https://bazaar.example/"), new string('a', 64)));
        });

        Assert.IsType<ServerIdentityCheck>(Assert.Single(provider.GetServices<ISetupVerificationCheck>()));
    }
}
