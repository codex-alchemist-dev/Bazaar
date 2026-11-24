using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// The per-agent hourly cap on approval requests (<see cref="ApprovalOptions.RequestCapPerAgentHour"/>). Dispatched requests
/// (<see cref="ApprovalService"/>) and enqueued ones (<see cref="OwnerApprovalEnqueuer"/>) both count here, in one bucket per
/// agent, so a runaway agent cannot fill the owner's card queue by either path.
/// </summary>
internal static class ApprovalRequestCap
{
    private static readonly TimeSpan _window = TimeSpan.FromHours(1);

    /// <summary>Counts one request for the agent. False when the agent has used its cap for the hour.</summary>
    public static bool TryConsume(IRateBucketStore buckets, string agentId, ApprovalOptions options, DateTimeOffset now) =>
        buckets.TryConsume($"approval-request|{agentId}", new RateRule(options.RequestCapPerAgentHour, _window), now).Allowed;
}
