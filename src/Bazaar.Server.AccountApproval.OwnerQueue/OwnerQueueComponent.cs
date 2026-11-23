using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.AccountApproval.OwnerQueue;

/// <summary>
/// Adapter component: sends account approval requests that need the owner to the owner's Tier 2 approval queue
/// (docs/design/15-security-taint-and-approvals.md, docs/design/59-desktop-density-and-guest-display.md section 7). It needs an
/// owner approval enqueuer from the host (Bazaar.Server.Security); it registers none.
/// </summary>
public sealed class OwnerQueueComponent : IBazaarComponent
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "account-approval-owner-queue",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["account-approval"],
        Provides: ["account-approval:owner-queue"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddBazaarAccountApprovalOwnerQueue();
    }
}
