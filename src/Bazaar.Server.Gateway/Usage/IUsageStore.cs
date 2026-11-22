using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>Append-only storage for <c>gw_usage</c> rows. Postgres implements it; tests use <see cref="InMemoryUsageStore"/>.</summary>
public interface IUsageStore
{
    /// <summary>Appends one usage row. Rows are never updated or deleted here.</summary>
    ValueTask AppendAsync(GatewayUsageRecord record, CancellationToken ct);
}
