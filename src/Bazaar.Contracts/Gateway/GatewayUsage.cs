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
}

/// <summary>How a call ended, for the usage record.</summary>
public enum UsageStatus
{
    Success,
    Error,

    /// <summary>The stream was cut off; the counts are partial and marked estimated.</summary>
    Aborted,
}

/// <summary>
/// One row of the <c>gw_usage</c> table: one per model call. No prompt or completion text is stored.
/// Cost is not stored; it is computed from a dated price table identified by <c>PriceVersion</c>.
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
    string? PriceVersion);
