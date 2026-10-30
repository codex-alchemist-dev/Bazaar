using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>Field-wise sums of token counts. Every bucket adds independently, so <c>Thinking</c> stays a subset of <c>Output</c>.</summary>
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
    public static long PromptTokens(GatewayTokenUsage t) => t.Input + t.CacheWrite5m + t.CacheWrite1h + t.CacheRead;
}
