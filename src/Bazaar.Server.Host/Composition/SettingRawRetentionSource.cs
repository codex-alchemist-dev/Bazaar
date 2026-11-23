using Bazaar.Server.Health;
using Bazaar.Server.Knowledge;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.Composition;

/// <summary>Reads <c>metrics.raw_retention_days</c> from the settings store for the Health module's retention sweep.</summary>
internal sealed class SettingRawRetentionSource(ISettingStore store) : IRawRetentionSource
{
    public Task<int> GetDaysAsync(CancellationToken cancellationToken = default) =>
        store.GetAsync(KnowledgeSettings.MetricsRawRetentionDays, cancellationToken: cancellationToken);
}
