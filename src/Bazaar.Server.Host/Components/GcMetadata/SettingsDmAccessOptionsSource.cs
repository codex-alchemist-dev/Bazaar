using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.GcMetadata;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.Components.GcMetadata;

/// <summary>
/// Reads <c>gc.dm_access_approvers</c> from the settings store on every call, so a change applies at once (docs/design/01, Settings).
/// The owner-read marker comes from <c>privacy.owner_dm_read_marker</c> (doc 32), on by default.
/// </summary>
internal sealed class SettingsDmAccessOptionsSource(ISettingStore settings) : IDmAccessOptionsSource
{
    public async ValueTask<DmAccessOptions> GetAsync(CancellationToken ct)
    {
        var approvers = await settings.GetAsync(GcSettings.DmAccessApproversKey, cancellationToken: ct).ConfigureAwait(false);
        var marker = await settings.GetAsync(GcSettings.OwnerDmReadMarkerKey, cancellationToken: ct).ConfigureAwait(false);
        return new DmAccessOptions(approvers == GcDmAccessApprovers.OwnerOrDmMember ? DmApprovers.OwnerOrDmMember : DmApprovers.Owner, marker);
    }
}
