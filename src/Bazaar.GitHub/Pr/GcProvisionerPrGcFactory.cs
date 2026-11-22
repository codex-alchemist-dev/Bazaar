using Bazaar.Contracts.Gc;
using Bazaar.GitHub.Ingress;
using Bazaar.Server.Gc.Write;

namespace Bazaar.GitHub.Pr;

/// <summary>
/// The real <see cref="IGitHubPrGcFactory"/>, over the GC module's <see cref="IGcProvisioner"/> (doc 40 section 5a; doc 21 PR GCs).
/// It creates the GC with the creator as its only member, then adds every other member one by one, because the provisioner's
/// create takes the owner alone. Both steps are idempotent, so a retry after a crash between them adds the members that are missing.
/// </summary>
public sealed class GcProvisionerPrGcFactory(IGcProvisioner provisioner, GitHubPrGcOptions options, GitHubIngressOptions ingress) : IGitHubPrGcFactory
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

    // Membership stores only the id today; the kind and name travel for the provisioner's contract.
    private GcPrincipal Principal(Guid id) =>
        id == options.OwnerPrincipalId ? new GcPrincipal(id, PrincipalKind.Owner, "owner")
        : id == ingress.EffectiveBridgePrincipalId ? new GcPrincipal(id, PrincipalKind.Agent, "github-bridge")
        : new GcPrincipal(id, PrincipalKind.Agent, "agent");
}
