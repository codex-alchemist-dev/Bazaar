using Bazaar.Contracts.Gc;
using Bazaar.GitHub.Ingress;
using Bazaar.Server.Gc.Write;

namespace Bazaar.GitHub.Issues;

/// <summary>The real <see cref="IGitHubIssueGcFactory"/>, over <see cref="IGcProvisioner"/>: create with the creator, then add each other member. Both steps are idempotent.</summary>
public sealed class GcProvisionerIssueGcFactory(IGcProvisioner provisioner, GitHubIssueGcOptions options, Guid? ownerPrincipalId, GitHubIngressOptions ingress) : IGitHubIssueGcFactory
{
    /// <inheritdoc />
    public async Task<bool> EnsureAsync(Guid gcId, string title, Guid createdBy, IReadOnlyList<Guid> members, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(metadata);
        var created = await provisioner.CreateAsync(gcId, options.GcTypeKey, title, Principal(createdBy), metadata, cancellationToken).ConfigureAwait(false);
        foreach (var member in members.Where(m => m != createdBy).Distinct())
        {
            await provisioner.AddMemberAsync(gcId, Principal(member), cancellationToken).ConfigureAwait(false);
        }

        return created;
    }

    /// <inheritdoc />
    public Task<bool> AddAgentAsync(Guid gcId, Guid agentId, string displayName, CancellationToken cancellationToken) =>
        provisioner.AddMemberAsync(gcId, new GcPrincipal(agentId, PrincipalKind.Agent, displayName), cancellationToken);

    /// <inheritdoc />
    public Task<bool> AddOwnerAsync(Guid gcId, Guid ownerId, CancellationToken cancellationToken) =>
        provisioner.AddMemberAsync(gcId, new GcPrincipal(ownerId, PrincipalKind.Owner, "owner"), cancellationToken);

    private GcPrincipal Principal(Guid id) =>
        id == ownerPrincipalId ? new GcPrincipal(id, PrincipalKind.Owner, "owner")
        : id == ingress.EffectiveBridgePrincipalId ? new GcPrincipal(id, PrincipalKind.Agent, "github-bridge")
        : new GcPrincipal(id, PrincipalKind.Agent, "agent");
}
