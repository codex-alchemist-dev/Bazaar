using Bazaar.Core;
using Bazaar.Server.VmBench.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmBench;

/// <summary>
/// The VM benchmark module (doc 16, "Testing"). It is measurement plumbing, not a VM feature: the Hyper-V host supplies
/// <see cref="IVmBenchHost"/> at run time, so the module has no hard dependencies and the VM module is a soft link only.
/// </summary>
public sealed class VmBenchComponent : IBazaarComponent
{
    public ComponentManifest Manifest { get; } = new(
        Id: "vm-bench",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["vm-bench:runner", "vm-bench:report"]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IVmBenchRunner, VmBenchRunner>();
        services.AddSingleton<IVmBenchReportWriter, VmBenchReportWriter>();
    }
}
