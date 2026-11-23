using Bazaar.Server.Host.Composition;
using Bazaar.Server.Vms.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ModuleAgentVmDirectory = Bazaar.Server.Vms.Abstractions.IAgentVmDirectory;
using ModuleVmControl = Bazaar.Server.Vms.Abstractions.IVmControl;

namespace Bazaar.Server.Host.Tests;

/// <summary>The Vms module's cross-module ports (<c>Bazaar.Server.Vms.Abstractions</c>) as the host registers them. No database is contacted.</summary>
public sealed class VmsCrossModulePortsTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? values = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=unused"));
        services.AddBazaarVms(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Without_a_backend_the_directory_exists_and_the_control_port_does_not()
    {
        using var provider = Build();

        Assert.IsType<EnvironmentAgentVmDirectory>(provider.GetRequiredService<ModuleAgentVmDirectory>());
        Assert.Null(provider.GetService<ModuleVmControl>());
    }

    [Fact]
    public void With_a_configured_backend_the_control_port_goes_through_the_lifecycle_service()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Bazaar:Vms:Kvm:ImageDirectory"] = "/var/lib/bazaar/images",
            ["Bazaar:Vms:Kvm:VmDirectory"] = "/var/lib/bazaar/vms",
        });

        Assert.IsType<LifecycleVmControl>(provider.GetRequiredService<ModuleVmControl>());
        Assert.IsType<EnvironmentAgentVmDirectory>(provider.GetRequiredService<ModuleAgentVmDirectory>());
    }

    [Fact]
    public void The_agent_pausers_directory_is_a_different_service_and_is_still_registered()
    {
        using var provider = Build();

        var older = provider.GetRequiredService<Bazaar.Server.AgentSafety.Containment.IAgentVmDirectory>();
        Assert.NotSame((object)provider.GetRequiredService<ModuleAgentVmDirectory>(), older);
    }
}
