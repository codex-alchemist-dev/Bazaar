using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.TalentPool.Component;

/// <summary>
/// The Talent whitelist, read from the gateway (docs/design/25: a template whose model is outside the whitelist is refused).
/// A model is allowed only when it is provider-qualified (<c>provider:model</c>) and the gateway lists it as available.
/// A template never widens an agent's own model policy: the agent gets exactly the manifest models, which already passed this check.
/// </summary>
public sealed class GatewayTalentModelWhitelist(IGatewayDirectory directory) : ITalentModelWhitelist
{
    /// <inheritdoc />
    public async ValueTask<bool> IsAllowedAsync(string model, CancellationToken cancellationToken)
    {
        if (!ModelRef.TryParse(model, out var reference))
        {
            return false;
        }

        var found = await directory.GetModelAsync(reference, cancellationToken).ConfigureAwait(false);
        return found is { Status: Gateway.Data.GwModel.StatusAvailable };
    }
}
