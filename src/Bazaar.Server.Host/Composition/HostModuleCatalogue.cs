using Bazaar.Core;
using Bazaar.Server.CompanyBuild.State;

namespace Bazaar.Server.Host.Composition;

/// <summary>The module catalogue of this host: the manifests of every component in <see cref="HostComponents.All"/>.</summary>
public sealed class HostModuleCatalogue : IModuleCatalogue
{
    public IReadOnlyList<ComponentManifest> Manifests { get; } = [.. HostComponents.All.Select(c => c.Manifest)];
}
