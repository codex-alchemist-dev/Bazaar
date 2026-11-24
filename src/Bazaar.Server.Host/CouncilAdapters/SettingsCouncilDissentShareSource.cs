using Bazaar.Server.Council.Service;
using Bazaar.Server.Knowledge;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.CouncilAdapters;

/// <summary>
/// <see cref="ICouncilDissentShareSource"/> over the <c>council.followup_dissent_share</c> setting (doc 10, Setting at decision time).
/// It reads the store on every call, so an operator change applies at the next tally without a restart. A stored value that no
/// longer validates throws (the store's rule), so a tally fails closed instead of using a damaged share.
/// </summary>
public sealed class SettingsCouncilDissentShareSource(ISettingStore settings) : ICouncilDissentShareSource
{
    /// <inheritdoc />
    public Task<decimal> GetFollowupDissentShareAsync(CancellationToken ct) =>
        settings.GetAsync(KnowledgeSettings.CouncilFollowupDissentShareKey, cancellationToken: ct);
}
