using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Calendar.Tests;

public sealed class CalendarComponentTests
{
    [Fact]
    public void Manifest_exposes_the_calendar_feature_and_requires_gc()
    {
        var manifest = new CalendarComponent().Manifest;

        Assert.Equal("calendar", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Equal(["gc"], manifest.DependsOn);
        Assert.Equal(["feature:company-calendar"], manifest.Provides);
    }

    [Fact]
    public void Registers_calendar_services()
    {
        var services = new ServiceCollection();

        new CalendarComponent().AddServices(services, new ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(CalendarRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(ICalendarService) && d.ImplementationType == typeof(CalendarService));
    }
}
