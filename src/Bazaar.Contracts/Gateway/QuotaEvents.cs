using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Gateway;

/// <summary>
/// The state of one provider key for one model class. Quota is tracked per key, never per agent,
/// because agents share a key.
/// </summary>
public enum QuotaState
{
    Available,
    RateLimited,
    SpendCapped,
    DailyExhausted,
    Down,
}

/// <summary>
/// A fact the gateway reports about quota. The gateway reports facts only; the Work-Life Simulation decides what they mean.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(QuotaStateChanged), "quota_state_changed")]
[JsonDerivedType(typeof(AgentCapacityChanged), "agent_capacity_changed")]
public abstract record GatewayQuotaEvent;

/// <summary>A provider key's state changed. <c>Until</c> is set for states that end at a known time.</summary>
public sealed record QuotaStateChanged(
    string ProviderId,
    string KeyRef,
    string ModelClass,
    QuotaState State,
    DateTimeOffset? Until) : GatewayQuotaEvent;

/// <summary>
/// Emitted only when every allowed candidate for an agent is blocked (or one becomes available again).
/// <c>Reason</c> is the state that blocked the last remaining candidate.
/// </summary>
public sealed record AgentCapacityChanged(
    string AgentId,
    bool Available,
    DateTimeOffset? Until,
    QuotaState Reason) : GatewayQuotaEvent;
