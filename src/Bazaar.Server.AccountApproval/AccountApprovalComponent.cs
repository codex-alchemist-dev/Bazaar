using Bazaar.Core;
using Bazaar.Server.AccountApproval.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AccountApproval;

/// <summary>
/// Peer approval of account creation and the outcome of a site vote (docs/design/15-security-taint-and-approvals.md,
/// docs/design/59-desktop-density-and-guest-display.md section 7). The host supplies the audit log
/// (<c>Bazaar.Server.Security.Audit.IAuditLog</c>) and <see cref="IOwnerApprovalHandOff"/>; the approver directory defaults to
/// "nobody is eligible", so a host that wires nothing sends every request to the owner. State is kept in
/// memory unless a host registers a durable <see cref="IAccountApprovalStore"/> first (<c>Bazaar.Server.AccountApproval.Postgres</c>).
/// </summary>
public sealed class AccountApprovalComponent : IBazaarComponent
{
    public ComponentManifest Manifest { get; } = new(
        Id: "account-approval",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["account-approval:peer-policy", "account-approval:site-access"]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AccountApprovalOptions>()
            .BindConfiguration(AccountApprovalOptions.SectionName)
            .Validate(o => o.Validate().Count == 0, "Account approval settings are invalid (peer quorum below two, bad expiry or blank approver ids).");
        services.TryAddSingleton<IApproverDirectory, NoApproverDirectory>();
        services.TryAddSingleton<IAccountApprovalStore, InMemoryAccountApprovalStore>();
        services.TryAddSingleton<AccountApprovalService>();
        services.TryAddSingleton<SiteAccessService>();
    }
}

/// <summary>Fail-closed default directory: nobody is eligible, so every request goes to the owner.</summary>
public sealed class NoApproverDirectory : IApproverDirectory
{
    public Task<ApproverStanding?> GetStandingAsync(string agentId, CancellationToken ct = default) =>
        Task.FromResult<ApproverStanding?>(null);
}
