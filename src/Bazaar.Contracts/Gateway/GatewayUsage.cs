namespace Bazaar.Contracts.Gateway;

/// <summary>
/// Token counts for one call. <c>Input</c> counts uncached input only and excludes cache reads and writes.
/// <c>Thinking</c> is a subset of <c>Output</c>. The two cache-write buckets stay separate because they are priced differently.
/// </summary>
public sealed record GatewayTokenUsage(
    long Input,
    long CacheWrite5m,
    long CacheWrite1h,
    long CacheRead,
    long Output,
    long Thinking);

/// <summary>Whose key paid for a Claude call (docs/design/05-model-gateway.md, claude lane).</summary>
public enum GatewayIdentity
{
    /// <summary>The owner's own provider key.</summary>
    Native,

    /// <summary>A pooled source such as FreeLLMAPI.</summary>
    Pooled,

    /// <summary>A Claude plan login held by the unmodified Claude Code CLI (docs/design/91-claude-code-cli-provider.md). Cost is notional.</summary>
    Subscription,
}

/// <summary>Text form of <see cref="GatewayIdentity"/> as stored in the gateway tables.</summary>
public static class GatewayIdentityText
{
    public static string ToText(this GatewayIdentity identity) => identity switch
    {
        GatewayIdentity.Native => "native",
        GatewayIdentity.Pooled => "pooled",
        GatewayIdentity.Subscription => "subscription",
        _ => throw new ArgumentOutOfRangeException(nameof(identity)),
    };

    public static GatewayIdentity FromText(string text) => text switch
    {
        "native" => GatewayIdentity.Native,
        "pooled" => GatewayIdentity.Pooled,
        "subscription" => GatewayIdentity.Subscription,
        _ => throw new InvalidDataException("Unknown gateway identity: " + text),
    };
}

/// <summary>How a call ended, for the usage record.</summary>
public enum UsageStatus
{
    Success,
    Error,

    /// <summary>The stream was cut off; the counts are partial and marked estimated.</summary>
    Aborted,

    /// <summary>Policy refused the call before any provider was contacted. The row carries zero tokens.</summary>
    Denied,
}

/// <summary>
/// One row of the <c>gw_usage</c> table: one per model call. No prompt or completion text is stored.
/// <c>Source</c> names where the call went (see <c>UsageSource</c> in the gateway). Cost is not stored; it is computed from a dated price table identified by <c>PriceVersion</c>.
/// </summary>
public sealed record GatewayUsageRecord(
    Guid Id,
    DateTimeOffset Timestamp,
    string AgentId,
    string? TurnId,
    string ProviderId,
    string KeyRef,
    string ModelId,
    string? ServedModel,
    GatewayIdentity Identity,
    GatewayTokenUsage Tokens,
    long LatencyMs,
    UsageStatus Status,
    string? ErrorCode,
    int? RetryAfterSeconds,
    bool Estimated,
    string? PriceVersion,
    string? Source = null);
