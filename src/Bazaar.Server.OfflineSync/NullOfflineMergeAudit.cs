using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync;

/// <summary>An audit sink that discards records. The default when no audit module is installed.</summary>
public sealed class NullOfflineMergeAudit : IOfflineMergeAudit
{
    /// <inheritdoc />
    public ValueTask RecordAsync(OfflineWrite write, OfflineWriteOutcome outcome, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
