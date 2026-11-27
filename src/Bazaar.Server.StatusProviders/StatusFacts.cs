namespace Bazaar.Server.StatusProviders;

/// <summary>Agent runtime counts (doc 06 tables: inbox_item, turn, agent).</summary>
public sealed record AgentFacts(long Agents, long InboxPending, long InboxClaimed, long TurnsRunning, long TurnsFailedRecently, long OldestPendingSeconds);

/// <summary>Gateway provider counts (doc 05 tables: gw_provider, gw_quota_state). A provider with no quota rows counts as available.</summary>
public sealed record GatewayFacts(long EnabledProviders, long ProvidersDown, long ProvidersLimited);

/// <summary>Group chat log reachability (doc 01 tables).</summary>
public sealed record GcFacts(long Groups, long EventsLastMinute);

/// <summary>The data seam: one read-only query per area. A throw means the database did not answer.</summary>
public interface IStatusFactsSource
{
    Task<AgentFacts> GetAgentsAsync(CancellationToken ct);

    Task<GatewayFacts> GetGatewayAsync(CancellationToken ct);

    Task<GcFacts> GetGcAsync(CancellationToken ct);
}
