using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Analytics;

/// <summary>
/// Builds the report for a viewer. Agents are listed in id order, never by any metric: no ranking (doc 25, Boundaries). Whether a
/// viewer sees any agent rows comes from <see cref="AnalyticsVisibility"/>; company totals are always shown.
/// </summary>
public sealed class AgentAnalyticsService(IWorkEventSource source, AnalyticsOptions options) : IAgentAnalytics
{
    public async Task<AnalyticsReport> ReadAsync(AnalyticsViewer viewer, AnalyticsWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(window);

        var events = await source.ReadAsync(window, ct).ConfigureAwait(false);
        var company = AgentMetricsCalculator.ForCompany(events, window);

        var agents = events
            .Where(e => window.Contains(e.At))
            .Select(e => e.AgentId)
            .Distinct(StringComparer.Ordinal)
            .Where(id => MayView(viewer, id))
            .Order(StringComparer.Ordinal)
            .Select(id => AgentMetricsCalculator.ForAgent(id, events, window))
            .ToArray();

        return new AnalyticsReport(window, company, agents);
    }

    private bool MayView(AnalyticsViewer viewer, string agentId) => viewer.IsOwner || options.AgentMetricsVisibility switch
    {
        AnalyticsVisibility.Peers => viewer.IsAgent,
        AnalyticsVisibility.OwnerAndSelf => viewer.IsAgent && viewer.Id == agentId,
        _ => false,
    };
}

/// <summary>Install settings for analytics (doc 25 Settings).</summary>
public sealed record AnalyticsOptions(AnalyticsVisibility AgentMetricsVisibility = AnalyticsVisibility.OwnerAndSelf);

/// <summary>
/// The default source: merges every registered <see cref="IWorkEventCollector"/>. With none registered every read is a gap. A
/// collector that throws is skipped and the rest still count, so one broken store cannot blank the pane.
/// </summary>
public sealed partial class CompositeWorkEventSource(IEnumerable<IWorkEventCollector> collectors, ILogger<CompositeWorkEventSource>? log = null) : IWorkEventSource
{
    public async Task<IReadOnlyList<AgentWorkEvent>> ReadAsync(AnalyticsWindow window, CancellationToken ct)
    {
        var all = new List<AgentWorkEvent>();
        foreach (var collector in collectors)
        {
            try
            {
                all.AddRange(await collector.CollectAsync(window, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (log is not null)
                {
                    CollectorFailed(log, ex, collector.GetType().Name);
                }
            }
        }

        return all;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analytics collector {Collector} failed and was skipped.")]
    private static partial void CollectorFailed(ILogger log, Exception ex, string collector);
}
