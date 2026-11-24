using Bazaar.Server.CompanyKnowledge;
using Bazaar.Server.CompanyKnowledge.Abstractions;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class CompanyKnowledgeComponentTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();
        var services = new ServiceCollection();
        new CompanyKnowledgeComponent().AddServices(services, configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Component_is_listed_and_registers_the_registry_as_the_candidate_source()
    {
        Assert.Contains(HostComponents.All, c => c is CompanyKnowledgeComponent);
        using var provider = Build();
        Assert.Same(provider.GetRequiredService<CompanyCandidateRegistry>(), provider.GetRequiredService<ICompanyCandidateProposers>());
        Assert.True(provider.GetRequiredService<ICompanyAgentQuarantine>().IsQuarantined("anyone"));
    }

    [Fact]
    public void Service_without_the_memory_vault_fails_with_a_message_naming_it()
    {
        using var provider = Build();
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<CompanyKnowledgeService>());
        Assert.Contains(nameof(IMemoryVault), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_bind_and_reject_values_that_do_not_parse()
    {
        using var provider = Build(("Bazaar:CompanyKnowledge:ReviewDays", "30"), ("Bazaar:CompanyKnowledge:Admission", "owner"));
        var options = provider.GetRequiredService<CompanyKnowledgeOptions>();
        Assert.Equal(30, options.ReviewDays);
        Assert.Equal(CompanyAdmissionAuthority.Owner, options.MinAuthority);

        Assert.Throws<InvalidOperationException>(() => Build(("Bazaar:CompanyKnowledge:Admission", "anyone")));
        Assert.Throws<InvalidOperationException>(() => Build(("Bazaar:CompanyKnowledge:ReviewDays", "-1")));
    }
}
