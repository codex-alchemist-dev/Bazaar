using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.StatusProviders;

/// <summary>
/// Real queue depths for the status page and the TUI (docs/design/51-web-admin-module.md section 5): the agent inbox and running
/// turns. A database that does not answer yields no rows here; the health source reports that as failing.
/// </summary>
public sealed class AgentQueueDepthSource(IStatusFactsSource facts) : IAdminQueueDepthSource
{
    public async Task<IReadOnlyList<AdminQueueDepth>> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var a = await facts.GetAgentsAsync(ct).ConfigureAwait(false);
            return [new("agent-inbox-pending", a.InboxPending), new("agent-inbox-claimed", a.InboxClaimed), new("agent-turns-running", a.TurnsRunning)];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }
}

/// <summary>Health checks for the agent runtime, the gateway and the GC log. Messages never carry exception text, so no connection detail leaks.</summary>
public sealed class CoreHealthSource(IStatusFactsSource facts) : IAdminHealthSource
{
    public const int StuckInboxSeconds = 600;

    public async Task<IReadOnlyList<AdminHealthCheck>> GetAsync(CancellationToken ct = default) =>
    [
        await SafeAsync("gc-log", async () => EvaluateGc(await facts.GetGcAsync(ct).ConfigureAwait(false))),
        await SafeAsync("gateway", async () => EvaluateGateway(await facts.GetGatewayAsync(ct).ConfigureAwait(false))),
        await SafeAsync("agents", async () => EvaluateAgents(await facts.GetAgentsAsync(ct).ConfigureAwait(false))),
    ];

    private static async Task<AdminHealthCheck> SafeAsync(string name, Func<Task<AdminHealthCheck>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new AdminHealthCheck(name, "failing", "database did not answer");
        }
    }

    internal static AdminHealthCheck EvaluateGc(GcFacts g) =>
        new("gc-log", "healthy", $"{g.Groups} group chats, {g.EventsLastMinute} events in the last minute");

    internal static AdminHealthCheck EvaluateGateway(GatewayFacts g)
    {
        if (g.EnabledProviders == 0)
        {
            return new("gateway", "unknown", "no providers enabled");
        }

        if (g.ProvidersDown >= g.EnabledProviders)
        {
            return new("gateway", "failing", $"all {g.EnabledProviders} providers are out of quota or down");
        }

        return g.ProvidersLimited > 0
            ? new("gateway", "degraded", $"{g.ProvidersLimited} of {g.EnabledProviders} providers limited")
            : new("gateway", "healthy", $"{g.EnabledProviders} providers available");
    }

    internal static AdminHealthCheck EvaluateAgents(AgentFacts a)
    {
        if (a.Agents == 0)
        {
            return new("agents", "unknown", "no agents yet");
        }

        if (a.OldestPendingSeconds > StuckInboxSeconds)
        {
            return new("agents", "degraded", $"oldest waiting wake is {a.OldestPendingSeconds / 60} minutes old");
        }

        return a.TurnsFailedRecently > 0
            ? new("agents", "degraded", $"{a.TurnsFailedRecently} failed or abandoned turns in the last 15 minutes")
            : new("agents", "healthy", $"{a.Agents} agents, {a.TurnsRunning} turns running");
    }
}
