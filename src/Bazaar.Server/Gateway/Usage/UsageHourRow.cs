using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// One <c>usage_hour</c> row: the calls one agent made to one provider model, through one identity, in one UTC hour.
/// <see cref="EstimatedCalls"/> counts calls whose tokens were estimated, so estimates are never summed silently with billed figures.
/// </summary>
public sealed record UsageHourRow(
    DateTimeOffset Hour,
    string AgentId,
    string ProviderId,
    string ModelId,
    GatewayIdentity Identity,
    int Calls,
    GatewayTokenUsage Tokens,
    int ErrorCalls,
    int EstimatedCalls);
