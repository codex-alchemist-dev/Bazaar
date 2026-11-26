using Bazaar.Server.Settings;

namespace Bazaar.Server.Constellation.Memory.Context;

/// <summary>
/// The <c>memory.context.*</c> limits (docs/design/constellation-memory-graph.md, "Retrieval operation", rate and size limits).
/// Defaults are the PROPOSED values; hard caps are not settings: a validator refuses a value above the cap, and the service clamps
/// again, so no stored value can loosen them.
/// </summary>
public static class MemoryContextSettings
{
    /// <summary>The hard cap of <see cref="MaxTokensKey"/>.</summary>
    public const int MaxTokensHardCap = 8000;

    /// <summary>The hard cap of <see cref="MaxHitsKey"/>.</summary>
    public const int MaxHitsHardCap = 50;

    /// <summary>The hard cap of <see cref="MaxQueryCharsKey"/>.</summary>
    public const int MaxQueryCharsHardCap = 8000;

    /// <summary>The hard cap of <see cref="RatePerAgentPerMinuteKey"/>.</summary>
    public const int RatePerAgentPerMinuteHardCap = 600;

    public static readonly SettingKey<int> MaxTokensKey = new(
        "memory.context.max_tokens", 2000, SettingScope.Install, SettingRole.Operator,
        $"Memory context: maximum excerpt tokens per retrieval (hard cap {MaxTokensHardCap}).",
        validate: Range(1, MaxTokensHardCap));

    public static readonly SettingKey<int> MaxHitsKey = new(
        "memory.context.max_hits", 12, SettingScope.Install, SettingRole.Operator,
        $"Memory context: maximum hits per retrieval (hard cap {MaxHitsHardCap}).",
        validate: Range(1, MaxHitsHardCap));

    public static readonly SettingKey<int> MaxQueryCharsKey = new(
        "memory.context.max_query_chars", 2000, SettingScope.Install, SettingRole.Operator,
        $"Memory context: longest query text accepted; longer queries are refused as too_large (hard cap {MaxQueryCharsHardCap}).",
        validate: Range(1, MaxQueryCharsHardCap));

    public static readonly SettingKey<int> RatePerAgentPerMinuteKey = new(
        "memory.context.rate_per_agent_per_minute", 30, SettingScope.Install, SettingRole.Operator,
        $"Memory context: retrievals per caller per minute; over the limit is refused as rate_limited (hard cap {RatePerAgentPerMinuteHardCap}).",
        validate: Range(1, RatePerAgentPerMinuteHardCap));

    /// <summary>Every <c>memory.context.*</c> key.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = [MaxTokensKey, MaxHitsKey, MaxQueryCharsKey, RatePerAgentPerMinuteKey];

    private static Func<int, string?> Range(int min, int max) =>
        value => value >= min && value <= max ? null : $"must be between {min} and {max}; got {value}.";
}

/// <summary>Registers <see cref="MemoryContextSettings.All"/> with the settings registry. The host composes it with the built-in keys.</summary>
public sealed class MemoryContextSettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => MemoryContextSettings.All;
}

/// <summary>The effective limits of one call, already clamped to the hard caps.</summary>
public sealed record MemoryContextLimits(int MaxTokens, int MaxHits, int MaxQueryChars, int RatePerAgentPerMinute)
{
    /// <summary>The shipped defaults.</summary>
    public static MemoryContextLimits Defaults { get; } = new(
        MemoryContextSettings.MaxTokensKey.Default,
        MemoryContextSettings.MaxHitsKey.Default,
        MemoryContextSettings.MaxQueryCharsKey.Default,
        MemoryContextSettings.RatePerAgentPerMinuteKey.Default);

    /// <summary>These limits with every value clamped to [1, hard cap].</summary>
    public MemoryContextLimits Clamped() => new(
        Math.Clamp(MaxTokens, 1, MemoryContextSettings.MaxTokensHardCap),
        Math.Clamp(MaxHits, 1, MemoryContextSettings.MaxHitsHardCap),
        Math.Clamp(MaxQueryChars, 1, MemoryContextSettings.MaxQueryCharsHardCap),
        Math.Clamp(RatePerAgentPerMinute, 1, MemoryContextSettings.RatePerAgentPerMinuteHardCap));
}

/// <summary>Reads the current limits.</summary>
public interface IMemoryContextLimitsSource
{
    Task<MemoryContextLimits> GetAsync(CancellationToken ct);
}

/// <summary>The limits from the host's setting store; each value is clamped to its hard cap.</summary>
public sealed class SettingStoreMemoryContextLimits(ISettingStore store) : IMemoryContextLimitsSource
{
    private readonly ISettingStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<MemoryContextLimits> GetAsync(CancellationToken ct) => new MemoryContextLimits(
        await _store.GetAsync(MemoryContextSettings.MaxTokensKey, cancellationToken: ct).ConfigureAwait(false),
        await _store.GetAsync(MemoryContextSettings.MaxHitsKey, cancellationToken: ct).ConfigureAwait(false),
        await _store.GetAsync(MemoryContextSettings.MaxQueryCharsKey, cancellationToken: ct).ConfigureAwait(false),
        await _store.GetAsync(MemoryContextSettings.RatePerAgentPerMinuteKey, cancellationToken: ct).ConfigureAwait(false)).Clamped();
}

/// <summary>Fixed limits, for installs without a setting store and for tests.</summary>
public sealed class FixedMemoryContextLimits(MemoryContextLimits limits) : IMemoryContextLimitsSource
{
    private readonly MemoryContextLimits _limits = (limits ?? throw new ArgumentNullException(nameof(limits))).Clamped();

    public Task<MemoryContextLimits> GetAsync(CancellationToken ct) => Task.FromResult(_limits);
}
