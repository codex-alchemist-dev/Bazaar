using Bazaar.Core;
using Bazaar.Server.Org.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Org.Tests;

public sealed class OrgComponentTests
{
    [Fact]
    public void Manifest_exposes_the_org_feature_and_requires_gc()
    {
        var manifest = new OrgComponent().Manifest;

        Assert.Equal("org", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Equal(["gc"], manifest.DependsOn);
        Assert.Equal(["feature:company-org"], manifest.Provides);
    }

    [Fact]
    public void Registers_org_services()
    {
        var services = new ServiceCollection();

        new OrgComponent().AddServices(services, new ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(OrgRelationRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(OrgLoanService));
    }
}
