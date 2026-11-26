using System.Globalization;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Observatory;

/// <summary>
/// Reads usage and lane state and shapes them for the dashboard and GC cost chips (doc 23, Usage accounting and Quota surfacing).
/// Cost is display only: there are no money budgets and no spend-cap percentage.
/// </summary>
public sealed class ObservatoryService(
    IUsageReader usage,
    IUsageCostCalculator prices,
    ILaneReader lanes,
    ObservatoryOptions options,
    TimeProvider clock) : IObservatoryService
{
    public async ValueTask<UsageSummary> SummarizeAsync(DateTimeOffset start, DateTimeOffset until, CancellationToken ct)
    {
        var rows = await usage.ReadRangeAsync(start, until, ct).ConfigureAwait(false);
        return new UsageSummary(
            start,
            until,
            Group("total", rows),
            GroupBy(rows, r => r.AgentId),
            GroupBy(rows, r => $"{r.ProviderId}:{r.ModelId}"),
            GroupBy(rows, r => SourceLabel(r.Identity)));
    }

    public async ValueTask<CostChip?> ChipAsync(string turnId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        var rows = await usage.ReadTurnAsync(turnId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        decimal cost = 0m;
        var known = true;
        long tokens = 0;
        var longPrompt = false;
        foreach (var row in rows)
        {
            tokens += row.Tokens.Input + row.Tokens.CacheWrite5m + row.Tokens.CacheWrite1h + row.Tokens.CacheRead + row.Tokens.Output;
            longPrompt |= Prompt(row.Tokens) > options.LongPromptTokens;
            if (prices.Price(row) is { } price)
            {
                cost += price;
            }
            else
            {
                known = false;
            }
        }

        return new CostChip(turnId, rows.Count, tokens, known ? cost : null, rows.Any(r => r.Estimated), longPrompt);
    }

    public async ValueTask<IReadOnlyList<LaneView>> LanesAsync(CancellationToken ct)
    {
        var snapshots = await lanes.ReadAsync(ct).ConfigureAwait(false);
        return [.. snapshots.Select(ToView).OrderBy(l => l.ProviderId, StringComparer.Ordinal).ThenBy(l => l.ModelClass, StringComparer.Ordinal)];
    }

    public async ValueTask<ObservatoryCard> CardAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var summary = await SummarizeAsync(dayStart, dayStart.AddDays(1), ct).ConfigureAwait(false);
        var laneViews = await LanesAsync(ct).ConfigureAwait(false);

        var est = options.EstimateLabel ? " (est.)" : string.Empty;
        List<ObservatoryItem> today =
        [
            new("Calls", summary.Total.Calls.ToString(CultureInfo.InvariantCulture)),
            new("Input tokens", summary.Total.Tokens.Input.ToString(CultureInfo.InvariantCulture)),
            new("Output tokens", summary.Total.Tokens.Output.ToString(CultureInfo.InvariantCulture)),
            new("Cost" + est, FormatCost(summary.Total)),
        ];
        if (summary.Total.ErrorCalls > 0)
        {
            today.Add(new("Errors", summary.Total.ErrorCalls.ToString(CultureInfo.InvariantCulture)));
        }

        List<ObservatorySection> sections =
        [
            new("Usage today", today),
            new("By source", [.. summary.BySource.Select(g => new ObservatoryItem(g.Key, $"{g.Calls} calls, {FormatCost(g)}"))]),
            new("By agent", [.. summary.ByAgent.Select(g => new ObservatoryItem(g.Key, $"{g.Calls} calls, {FormatCost(g)}"))]),
            new("Lanes", [.. laneViews.Select(l => new ObservatoryItem($"{l.ProviderId} {l.ModelClass}", FormatLane(l)))]),
        ];
        return new ObservatoryCard(sections);
    }

    /// <summary>The lowest remaining percent across the reported figures; near limit below the setting or when the state is not available.</summary>
    internal LaneView ToView(LaneSnapshot s)
    {
        int? percent = null;
        foreach (var (remaining, limit) in new[]
        {
            (s.RemainingRequests, s.LimitRequests),
            (s.RemainingInputTokens, s.LimitInputTokens),
            (s.RemainingOutputTokens, s.LimitOutputTokens),
        })
        {
            if (remaining is { } r && limit is { } l and > 0)
            {
                var p = (int)Math.Clamp(r * 100 / l, 0, 100);
                percent = percent is { } current ? Math.Min(current, p) : p;
            }
        }

        var near = s.State != QuotaState.Available || (percent is { } value && value < options.NearLimitPercent);
        return new LaneView(s.ProviderId, s.ModelClass, s.State, s.Until, percent, near);
    }

    private static string FormatLane(LaneView l)
    {
        var text = l.State == QuotaState.Available ? "ok" : l.State.ToString();
        if (l.RemainingPercent is { } p)
        {
            text += $", {p}% left";
        }

        return l.NearLimit ? text + ", near limit" : text;
    }

    private static string FormatCost(UsageGroup g) =>
        g.UnpricedCalls == g.Calls && g.Calls > 0
            ? "unknown"
            : g.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture) + (g.IsCostKnown ? string.Empty : "+ (some unpriced)");

    private static string SourceLabel(GatewayIdentity identity) => identity.ToString().ToLowerInvariant();

    private static long Prompt(GatewayTokenUsage t) => t.Input + t.CacheWrite5m + t.CacheWrite1h + t.CacheRead;

    private IReadOnlyList<UsageGroup> GroupBy(IReadOnlyList<GatewayUsageRecord> rows, Func<GatewayUsageRecord, string> key) =>
        [.. rows.GroupBy(key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Group(g.Key, g))];

    private UsageGroup Group(string key, IEnumerable<GatewayUsageRecord> rows)
    {
        int calls = 0, errors = 0, estimated = 0, unpriced = 0;
        decimal cost = 0m;
        long input = 0, w5 = 0, w1 = 0, read = 0, output = 0, thinking = 0;
        foreach (var r in rows)
        {
            calls++;
            input += r.Tokens.Input;
            w5 += r.Tokens.CacheWrite5m;
            w1 += r.Tokens.CacheWrite1h;
            read += r.Tokens.CacheRead;
            output += r.Tokens.Output;
            thinking += r.Tokens.Thinking;
            if (r.Status == UsageStatus.Error)
            {
                errors++;
            }

            if (r.Estimated)
            {
                estimated++;
            }

            if (prices.Price(r) is { } p)
            {
                cost += p;
            }
            else
            {
                unpriced++;
            }
        }

        return new UsageGroup(key, calls, new GatewayTokenUsage(input, w5, w1, read, output, thinking), errors, estimated, cost, unpriced);
    }
}
