using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.VmBench.Display;

/// <summary>
/// The doc 59 display and density benchmark scenarios (section 10, work package 7). Measurement plumbing only: the host supplies the
/// ports (display, density governor, browser pool, memory, targets) and a scenario whose port is missing reports "not run".
/// All links are soft, so the module loads on a host that has none of the others.
/// </summary>
public sealed class DisplayBenchComponent : ICdaxModule
{
    public ComponentManifest Manifest { get; } = new(
        Id: "vm-bench-display",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["vm-bench-display:runner", "vm-bench-display:report"],
        SoftDependsOn: ["vm-bench", "vm-display", "vm-density", "browser-pool"]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DisplayBenchCriteria>()
            .Bind(configuration.GetSection(DisplayBenchCriteria.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DisplayBenchCriteria>, DisplayBenchCriteriaValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDisplayBenchScenario, DesktopIdleRamScenario>();
        services.AddSingleton<IDisplayBenchScenario, BrowserRamScenario>();
        services.AddSingleton<IDisplayBenchScenario, ScreenshotLatencyScenario>();
        services.AddSingleton<IDisplayBenchScenario, InputLatencyScenario>();
        services.AddSingleton<IDisplayBenchScenario, DensityRampScenario>();
        services.AddSingleton<IDisplayBenchRunner, DisplayBenchRunner>();
        services.AddSingleton<IDisplayBenchReportWriter, DisplayBenchReportWriter>();
    }
}
