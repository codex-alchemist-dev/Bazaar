using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Mentions;

/// <summary>Setting <c>gc.broadcast_mentions</c>: who may use @here and @all.</summary>
public enum BroadcastPolicy
{
    /// <summary>Only the owner may broadcast.</summary>
    OwnerOnly,

    /// <summary>The owner may broadcast; agents may, up to a rate per hour. This is the default.</summary>
    OwnerAndAgentsRateLimited,
}

/// <summary>Why a broadcast was refused, or <see cref="None"/> when it was allowed.</summary>
public enum BroadcastDenial
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>Human co-members never broadcast, under either policy.</summary>
    HumanMemberNotAllowed,

    /// <summary>The policy is <see cref="BroadcastPolicy.OwnerOnly"/> and the author is an agent.</summary>
    AgentNotAllowedByPolicy,

    /// <summary>The agent has already used its broadcasts for the past hour.</summary>
    RateLimited,
}

/// <summary>Outcome of a broadcast check.</summary>
/// <param name="Allowed">True when the @here or @all may be delivered.</param>
/// <param name="Denial">The refusal reason, or <see cref="BroadcastDenial.None"/>.</param>
public sealed record BroadcastCheck(bool Allowed, BroadcastDenial Denial);

/// <summary>
/// Decides whether an author may use @here or @all. Broadcast is a separate check, not a principal
/// span. Decision: human co-members cannot broadcast under either policy; only the owner and agents
/// can, and agents are limited by the policy and the hourly rate.
/// </summary>
public static class BroadcastPolicyRules
{
    /// <summary>Checks one broadcast attempt.</summary>
    /// <param name="author">Who is trying to broadcast.</param>
    /// <param name="policy">The current <c>gc.broadcast_mentions</c> setting.</param>
    /// <param name="broadcastsInLastHour">Broadcasts the author already made in the trailing hour.</param>
    /// <param name="ratePerHour">The current <c>gc.broadcast_mention_rate_per_hour</c> setting.</param>
    public static BroadcastCheck CanBroadcast(GcViewer author, BroadcastPolicy policy, int broadcastsInLastHour, int ratePerHour)
    {
        ArgumentNullException.ThrowIfNull(author);

        switch (author.Kind)
        {
            case PrincipalKind.Owner:
                return Allow();

            case PrincipalKind.Human:
                return Deny(BroadcastDenial.HumanMemberNotAllowed);

            case PrincipalKind.Agent:
                if (policy == BroadcastPolicy.OwnerOnly)
                {
                    return Deny(BroadcastDenial.AgentNotAllowedByPolicy);
                }

                return broadcastsInLastHour < ratePerHour
                    ? Allow()
                    : Deny(BroadcastDenial.RateLimited);

            default:
                return Deny(BroadcastDenial.HumanMemberNotAllowed);
        }
    }

    private static BroadcastCheck Allow() => new(true, BroadcastDenial.None);

    private static BroadcastCheck Deny(BroadcastDenial denial) => new(false, denial);
}
