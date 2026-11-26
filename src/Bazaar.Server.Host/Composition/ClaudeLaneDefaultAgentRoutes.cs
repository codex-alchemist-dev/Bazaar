using Bazaar.Server.Gateway.ClaudeLane;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Selection;
using Bazaar.Server.Setup.FirstRun;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The first agent's routes (docs/design/92-endpoint-selection.md, "Claude lane route"; doc 45 section 1.15). The whitelist is the
/// members of the Claude lane route, optionally narrowed to the requested source ids, as <c>provider:model</c> names. Asking for a
/// profile binds that profile to the lane route, so the wizard's choice takes effect. Only what the gateway lists is returned; a
/// source it does not have is dropped, never invented.
/// </summary>
public sealed class ClaudeLaneDefaultAgentRoutes(
    IGatewayDirectory directory,
    IAgentSelectionSetup selection,
    IOptions<ClaudeLaneOptions> lane) : IDefaultAgentRoutes
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetWhitelistAsync(IReadOnlyList<string> requestedSources, string profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedSources);
        var options = lane.Value;
        var route = await directory.GetRouteAsync(options.RouteName, cancellationToken).ConfigureAwait(false);
        if (route is null) return [];

        var members = route.Members
            .Where(member => requestedSources.Count == 0 || requestedSources.Contains(member.ProviderId, StringComparer.Ordinal))
            .Select(member => member.ToString())
            .ToList();
        if (members.Count == 0) return [];

        var known = await selection.ListProfilesAsync(cancellationToken).ConfigureAwait(false);
        var chosen = known.Contains(profile, StringComparer.Ordinal) ? profile : options.Profile;
        await selection.BindAsync(options.RouteName, new SelectionBinding(chosen, IdentityTiers: true, NativeFirst: options.NativeFirst), cancellationToken).ConfigureAwait(false);
        return members;
    }
}
