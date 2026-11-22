using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Numerics;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// Field-wise sums of token counts. Every bucket adds independently, so <c>Thinking</c> stays a subset of <c>Output</c>.
/// <see cref="TokenAmount"/> sums are exact and cannot wrap (doc 48, DECIDED 11.3).
/// </summary>
internal static class TokenSums
{
    public static GatewayTokenUsage Add(GatewayTokenUsage a, GatewayTokenUsage b) => new(
        a.Input + b.Input,
        a.CacheWrite5m + b.CacheWrite5m,
        a.CacheWrite1h + b.CacheWrite1h,
        a.CacheRead + b.CacheRead,
        a.Output + b.Output,
        a.Thinking + b.Thinking);

    /// <summary>The whole prompt sent in one call: uncached input plus both cache-write buckets and cache reads.</summary>
    public static TokenAmount PromptTokens(GatewayTokenUsage t) => t.Input + t.CacheWrite5m + t.CacheWrite1h + t.CacheRead;

    /// <summary>Prompt plus output. <c>Thinking</c> is inside <c>Output</c>, so it is not added again.</summary>
    public static TokenAmount TotalTokens(GatewayTokenUsage t) => PromptTokens(t) + t.Output;
}
