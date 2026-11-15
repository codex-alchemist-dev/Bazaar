using Bazaar.Server.Calendar;
using Bazaar.Server.Company;
using Bazaar.Server.Company.Org;
using Bazaar.Server.Components;
using Bazaar.Server.Org.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Company;

public sealed class CompanyComponentTests
{
    [Fact]
    public void Manifest_exposes_company_features_and_requires_gc()
    {
        var manifest = new CompanyComponent().Manifest;

        Assert.Equal("company", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Equal(["gc"], manifest.DependsOn);
        Assert.Equal(["feature:company-calendar", "feature:company-org"], manifest.Provides);
    }

    [Fact]
    public void Registers_calendar_and_org_services()
    {
        var services = new ServiceCollection();

        new CompanyComponent().AddServices(services, new ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(CalendarRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(ICalendarService) && d.ImplementationType == typeof(CalendarService));
        Assert.Contains(services, d => d.ServiceType == typeof(OrgRelationRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(OrgLoanService));
    }
}
