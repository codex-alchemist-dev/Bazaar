namespace Bazaar.Server.Analytics;

/// <summary>Where work events come from. The Kanban, review and Git modules each contribute one; the default has none.</summary>
public interface IWorkEventSource
{
    Task<IReadOnlyList<AgentWorkEvent>> ReadAsync(AnalyticsWindow window, CancellationToken ct);
}

/// <summary>Reads company analytics for a viewer, applying the visibility setting.</summary>
public interface IAgentAnalytics
{
    Task<AnalyticsReport> ReadAsync(AnalyticsViewer viewer, AnalyticsWindow window, CancellationToken ct);
}

/// <summary>
/// One contributor of work events (Kanban, reputation outcomes, Git). A collector reads only; it never writes to the store it reads.
/// Every registered collector feeds the default <see cref="IWorkEventSource"/>.
/// </summary>
public interface IWorkEventCollector
{
    Task<IReadOnlyList<AgentWorkEvent>> CollectAsync(AnalyticsWindow window, CancellationToken ct);
}
