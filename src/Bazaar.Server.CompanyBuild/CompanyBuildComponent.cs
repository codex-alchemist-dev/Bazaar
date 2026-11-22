using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Bazaar.Server.CompanyBuild.Settings;
using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CompanyBuild;

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
        // The composer and runner are stateless statics. Only the operator settings are registered.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, CompanyBuildSettingContribution>());
    }
}
