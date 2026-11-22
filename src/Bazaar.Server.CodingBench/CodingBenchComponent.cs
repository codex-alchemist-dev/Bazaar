using Bazaar.Core;
using Bazaar.Server.CodingBench.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CodingBench;

/// <summary>
/// The coding-bench module: the benchmark runner and its report writer. It has no hard dependencies and sits outside Bazaar.Server.
/// The harness supplies the task source, agent runner and verifier; the approval defaults to deny until the owner approves.
/// </summary>
public sealed class CodingBenchComponent : IBazaarComponent
{
    public ComponentManifest Manifest { get; } = new(
        Id: "coding-bench",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["coding-bench:runner", "coding-bench:report"]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICodingBenchRunner, CodingBenchRunner>();
        services.AddSingleton<ICodingBenchReportWriter, CodingBenchReportWriter>();
        services.TryAddSingleton<ICodingBenchApproval, DenyUntilApprovedCodingBenchApproval>();
    }
}
