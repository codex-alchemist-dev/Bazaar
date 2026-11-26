using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Catalogue;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.Composition;

/// <summary>Reads <c>gateway.catalogue.refresh_hours</c> from the settings store for the catalogue refresh service.</summary>
internal sealed class SettingCatalogueSchedule(ISettingStore store) : ICatalogueSchedule
{
    public Task<int> GetRefreshHoursAsync(CancellationToken cancellationToken = default) =>
        store.GetAsync(GatewaySettings.CatalogueRefreshHours, cancellationToken: cancellationToken);
}

/// <summary>The schedule when no settings store is registered: no scheduled refresh.</summary>
internal sealed class NoCatalogueSchedule : ICatalogueSchedule
{
    public Task<int> GetRefreshHoursAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}
