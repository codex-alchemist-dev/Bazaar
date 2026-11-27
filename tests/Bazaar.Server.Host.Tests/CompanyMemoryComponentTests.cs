using Cdax.Core;
using Bazaar.Server.CompanyMemory;
using Bazaar.Server.Host.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class CompanyMemoryComponentTests
{
    [Fact]
    public void Host_ships_the_company_memory_component_with_a_soft_vault_dependency()
    {
        Assert.Contains(HostComponents.All, c => c.Manifest.Id == "company-memory");
        Assert.Empty(CompanyMemoryComponent.ManifestDefinition.DependsOn);
        Assert.Equal(["vault"], CompanyMemoryComponent.ManifestDefinition.Soft);

        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();
        Assert.Contains("company-memory", order);
    }

    [Fact]
    public async Task Without_a_vault_the_in_memory_store_records_and_reads_a_chain()
    {
        var services = new ServiceCollection();
        new CompanyMemoryComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryCompanyDecisionStore>(provider.GetRequiredService<ICompanyDecisionStore>());
        var memory = provider.GetRequiredService<ICompanyMemory>();
        await memory.RecordDecisionAsync(new(Guid.NewGuid(), "a", "A", "Body.", "agent-ana"), TestContext.Current.CancellationToken);

        Assert.NotNull(await memory.GetChainAsync("0001", TestContext.Current.CancellationToken));
    }
}
