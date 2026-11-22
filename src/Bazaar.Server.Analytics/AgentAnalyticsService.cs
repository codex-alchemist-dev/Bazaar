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

/// <summary>The default source: no collectors registered yet, so every read is a gap.</summary>
public sealed class EmptyWorkEventSource : IWorkEventSource
{
    public Task<IReadOnlyList<AgentWorkEvent>> ReadAsync(AnalyticsWindow window, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgentWorkEvent>>([]);
}
