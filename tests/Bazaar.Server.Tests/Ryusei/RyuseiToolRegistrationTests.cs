using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Kanban.Ryusei;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>Which RyuseiFuda agent tools the component registers, without a database (doc 38, WP-R9).</summary>
public sealed class RyuseiToolRegistrationTests
{
    [Fact]
    public void No_agent_tools_are_registered_without_a_project_id()
    {
        var services = Build(new Dictionary<string, string?>());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ToolRegistration));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IRyuseiAgentDirectory));
    }

    [Fact]
    public void A_malformed_project_id_registers_no_agent_tools()
    {
        var services = Build(new Dictionary<string, string?> { ["Bazaar:Ryusei:ProjectId"] = "not-a-guid" });

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ToolRegistration));
    }

    [Fact]
    public void The_four_ryusei_tools_are_registered_under_their_model_names_with_a_project_id()
    {
        var services = Build(new Dictionary<string, string?> { ["Bazaar:Ryusei:ProjectId"] = Guid.NewGuid().ToString() });

        Assert.Equal(4, services.Count(d => d.ServiceType == typeof(ToolRegistration)));
        Assert.Single(services, d => d.ServiceType == typeof(IRyuseiAgentDirectory));
        Assert.Equal(4, services.Count(d => d.ServiceType == typeof(ToolCapability)));
    }

    [Fact]
    public void No_capabilities_are_registered_without_a_project_id()
    {
        var services = Build(new Dictionary<string, string?>());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ToolCapability));
    }

    [Fact]
    public void Each_ryusei_tool_has_one_definition_with_a_project_id_and_none_without()
    {
        Assert.DoesNotContain(Build(new Dictionary<string, string?>()), d => d.ServiceType == typeof(ModelToolDefinition));

        var services = Build(new Dictionary<string, string?> { ["Bazaar:Ryusei:ProjectId"] = Guid.NewGuid().ToString() });
        var names = services.Where(d => d.ServiceType == typeof(ModelToolDefinition))
            .Select(d => ((ModelToolDefinition)d.ImplementationInstance!).Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["ryusei.cancel", "ryusei.list", "ryusei.pause", "ryusei.schedule"], names);
    }

    [Fact]
    public void The_ryusei_definitions_pass_the_catalogue_checks()
    {
        var registry = new ToolRegistry(RyuseiToolContract.Definitions.Select(d => (d.Name, (ITool)new NoTool())));

        Assert.Equal(4, ToolCatalogue.Build(RyuseiToolContract.Definitions, registry).Count);
    }

    private sealed class NoTool : ITool
    {
        public Task<string> ExecuteAsync(System.Text.Json.JsonElement args, CancellationToken cancellationToken) => Task.FromResult("{}");
    }

    private static List<ServiceDescriptor> Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        new RyuseiScheduleComponent().AddServices(services, configuration);
        return services.ToList();
    }
}
