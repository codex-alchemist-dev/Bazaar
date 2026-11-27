using Cdax.Core;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmDisplay.Audit;

/// <summary>
/// Joins the display module to the audit log as a Bazaar Component (docs/design/59, section 4, "IDisplayAuditSink: audit rows").
/// It registers <see cref="AuditLogDisplayAuditSink"/> as the <see cref="IDisplayAuditSink"/>, replacing the display module's no-op
/// default whichever component is added first. It does not touch <see cref="IDisplayGate"/>: the gate needs the policy engine, the
/// session taint store and the kill switch, which live in Bazaar.Server and have no abstractions project yet.
/// </summary>
public sealed class VmDisplayAuditComponent : ICdaxModule
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-display-audit",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["vm-display"],
        Provides: ["vm-display:audit"],
        SoftDependsOn: ["audit"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        DisplayAuditOptions options = DisplayAuditOptions.Bind(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // Replace, not TryAdd: the display module registers its no-op sink with TryAdd, so a replace works in either order, and a
        // later host registration still wins. The audit log is resolved at call time; without one every write fails closed.
        services.Replace(ServiceDescriptor.Singleton<IDisplayAuditSink>(sp => new AuditLogDisplayAuditSink(
            sp.GetService<IAuditLog>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<DisplayAuditOptions>())));
    }
}
