using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync;

/// <summary>The real server clock.</summary>
public sealed class SystemOfflineClock : IOfflineClock
{
    /// <inheritdoc />
    public long NowMillis => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
