using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.CompanyBuild;

/// <summary>
/// The company-build module. It composes a company's modules through the kernel graph and runs the build with bounded retries.
/// It has no hard dependencies and sits outside Bazaar.Server, so the server never gains build code.
/// </summary>
public sealed class CompanyBuildComponent : IBazaarComponent
{
    public ComponentManifest Manifest { get; } = new(
        Id: "company-build",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["company-build:composer", "company-build:runner"]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Stateless statics: nothing to register yet. The module exposes CompanyBuildComposer and CompanyBuildRunner directly.
    }
}
