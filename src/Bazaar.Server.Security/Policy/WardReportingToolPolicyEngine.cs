using System.Collections.Concurrent;
using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.Security.Policy;

/// <summary>Options for <see cref="WardReportingToolPolicyEngine"/>.</summary>
public sealed class DenialWindowOptions
{
    /// <summary>
    /// How long a denial counts toward "repeated denials" (doc 26 and doc 15 give no window). Shipped default: 10 minutes
    /// [PROPOSED]. The count threshold itself is the ward's <c>ward.denied_call_threshold</c>.
    /// </summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Wraps a <see cref="IToolPolicyEngine"/> and tells the Psych Ward about repeated denials (doc 26, section 1). The inner decision is
/// returned first and unchanged; the report is best effort and never throws. A rate-limit denial is a quota, not an anomaly, so it
/// is not counted. Counts are kept in memory per agent and reset on restart. Agent ids that are not GUIDs are not reported.
/// </summary>
public sealed class WardReportingToolPolicyEngine(IToolPolicyEngine inner, IWardTriggerIntake ward, DenialWindowOptions options) : IToolPolicyEngine
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _denials = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public PolicyDecision Evaluate(DispatchContext context)
    {
        var decision = inner.Evaluate(context);
        if (decision.Outcome == PolicyOutcome.Deny && decision.Reason != PolicyReason.RateLimited
            && Guid.TryParse(context.AgentId, out var agentId))
        {
            var count = Record(context.AgentId, context.Now);
            _ = ReportAsync(agentId, count, context.Tool);
        }

        return decision;
    }

    private int Record(string agentId, DateTimeOffset now)
    {
        var queue = _denials.GetOrAdd(agentId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            queue.Enqueue(now);
            while (queue.Count > 0 && now - queue.Peek() > options.Window)
            {
                queue.Dequeue();
            }

            return queue.Count;
        }
    }

    private async Task ReportAsync(Guid agentId, int count, string tool)
    {
        try
        {
            await ward.ReportDeniedCallsAsync(agentId, count, $"tool {tool}", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reporting is best effort.
        }
    }
}
