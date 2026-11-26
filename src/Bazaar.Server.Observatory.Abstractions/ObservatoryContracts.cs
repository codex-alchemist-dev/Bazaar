using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Observatory;

/// <summary>Reads <c>gw_usage</c> rows (owned by the gateway, doc 05). Postgres implements it; tests use fakes.</summary>
public interface IUsageReader
{
    /// <summary>Rows with a timestamp in <c>[start, until)</c>.</summary>
    ValueTask<IReadOnlyList<GatewayUsageRecord>> ReadRangeAsync(DateTimeOffset start, DateTimeOffset until, CancellationToken ct);

    /// <summary>Every row of one turn, for the GC cost chip.</summary>
    ValueTask<IReadOnlyList<GatewayUsageRecord>> ReadTurnAsync(string turnId, CancellationToken ct);
}

/// <summary>Prices one usage row from the dated price table (doc 23, Usage accounting). Null means unknown, never zero.</summary>
public interface IUsageCostCalculator
{
    decimal? Price(GatewayUsageRecord record);
}

/// <summary>Reads provider lane state from <c>gw_quota_state</c> (doc 05).</summary>
public interface ILaneReader
{
    ValueTask<IReadOnlyList<LaneSnapshot>> ReadAsync(CancellationToken ct);
}

/// <summary>The observed rate-limit figures for one lane. A null pair means the provider did not report it.</summary>
public sealed record LaneSnapshot(
    string ProviderId,
    string ModelClass,
    QuotaState State,
    DateTimeOffset? Until,
    long? RemainingRequests,
    long? LimitRequests,
    long? RemainingInputTokens,
    long? LimitInputTokens,
    long? RemainingOutputTokens,
    long? LimitOutputTokens);

/// <summary>A lane as the Observatory shows it. <see cref="RemainingPercent"/> is the lowest of the reported figures.</summary>
public sealed record LaneView(
    string ProviderId,
    string ModelClass,
    QuotaState State,
    DateTimeOffset? Until,
    int? RemainingPercent,
    bool NearLimit);

/// <summary>Totals for one group. Cost covers priced calls only; <see cref="UnpricedCalls"/> above zero means the cost is partial.</summary>
public sealed record UsageGroup(
    string Key,
    int Calls,
    GatewayTokenUsage Tokens,
    int ErrorCalls,
    int EstimatedCalls,
    decimal CostUsd,
    int UnpricedCalls)
{
    public bool IsCostKnown => UnpricedCalls == 0;
}

/// <summary>Usage over a window, split by agent, model and source (identity). No money budgets exist; cost is display only.</summary>
public sealed record UsageSummary(
    DateTimeOffset From,
    DateTimeOffset To,
    UsageGroup Total,
    IReadOnlyList<UsageGroup> ByAgent,
    IReadOnlyList<UsageGroup> ByModel,
    IReadOnlyList<UsageGroup> BySource);

/// <summary>The cost chip on a GC message: what one turn cost. A null cost is unknown.</summary>
public sealed record CostChip(
    string TurnId,
    int Calls,
    long TotalTokens,
    decimal? CostUsd,
    bool IsEstimate,
    bool LongPrompt);

/// <summary>One dashboard line, the shape <c>Bazaar.Client.Dashboard</c> lays out.</summary>
public sealed record ObservatoryItem(string Label, string Value);

/// <summary>One dashboard section.</summary>
public sealed record ObservatorySection(string Title, IReadOnlyList<ObservatoryItem> Items);

/// <summary>The dashboard model: <c>{ "sections": [ { "title", "items": [ { "label", "value" } ] } ] }</c>.</summary>
public sealed record ObservatoryCard(IReadOnlyList<ObservatorySection> Sections);

/// <summary>The Observatory read side: usage, cost chips, lanes and the dashboard card.</summary>
public interface IObservatoryService
{
    ValueTask<UsageSummary> SummarizeAsync(DateTimeOffset start, DateTimeOffset until, CancellationToken ct);

    ValueTask<CostChip?> ChipAsync(string turnId, CancellationToken ct);

    ValueTask<IReadOnlyList<LaneView>> LanesAsync(CancellationToken ct);

    /// <summary>The dashboard card for the UTC day containing now: usage today by source and agent, plus lanes.</summary>
    ValueTask<ObservatoryCard> CardAsync(CancellationToken ct);
}

/// <summary>Operator settings (doc 23 Settings). Bind from <c>Bazaar:Observatory</c>.</summary>
public sealed record ObservatoryOptions
{
    /// <summary>A lane is near limit when remaining is under this percent. 1 to 100.</summary>
    public int NearLimitPercent { get; init; } = 20;

    /// <summary>Prompt size above which a chip warns, because the price-step scope is unverified.</summary>
    public long LongPromptTokens { get; init; } = 100_000;

    /// <summary>Whether totals carry an "est." label. Turning it off hides that totals are estimates.</summary>
    public bool EstimateLabel { get; init; } = true;
}
