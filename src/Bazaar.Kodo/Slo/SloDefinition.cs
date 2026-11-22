using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Slo;

/// <summary>How an SLO reads its samples (doc 46 section 12.3).</summary>
public enum SloIndicatorKind
{
    /// <summary>
    /// Time based. Each store slot is one verdict from its state samples: bad if any sample was bad, else unobserved if any was, else
    /// good. A slot with no sample at all, after the first one recorded, is unobserved.
    /// </summary>
    Availability,

    /// <summary>
    /// Count based. The ratio of samples at or under a duration threshold. An empty slot adds nothing (no traffic is not an outage); a
    /// meter that cannot see records <see cref="SloSampleOutcome.Unobserved"/> samples instead.
    /// </summary>
    LatencyThreshold,
}

/// <summary>
/// One SLO (doc 46 section 12.3): an id, a target ratio, a rolling window and an indicator. Identifiers and numbers only, so a status
/// built from it carries no free text. The target is parts per million so the budget needs no floating point.
/// </summary>
public sealed record SloDefinition
{
    /// <summary>The lowest target a definition may have: 90 percent. A lower target is not a reliability objective.</summary>
    public const int MinTargetPpm = 900_000;

    /// <summary>The highest target: 99.9999 percent. A target of 100 percent has no budget.</summary>
    public const int MaxTargetPpm = 999_999;

    /// <summary>The default window (section 12.3): 30 days.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <summary>The shortest window.</summary>
    public static readonly TimeSpan MinWindow = TimeSpan.FromHours(1);

    /// <summary>The longest window, which is the most history the in-memory store keeps at its default resolution.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(31);

    /// <summary>The longest latency threshold.</summary>
    public static readonly TimeSpan MaxLatencyThreshold = TimeSpan.FromMinutes(1);

    /// <summary>Creates a definition, or throws <see cref="ArgumentException"/> or <see cref="ArgumentOutOfRangeException"/>.</summary>
    /// <param name="id">An identifier: lowercase letters, digits and underscores, starting with a letter, up to 64.</param>
    /// <param name="targetPpm">The target ratio in parts per million, from <see cref="MinTargetPpm"/> to <see cref="MaxTargetPpm"/>.</param>
    /// <param name="window">The rolling window, from <see cref="MinWindow"/> to <see cref="MaxWindow"/>.</param>
    /// <param name="kind">The indicator kind.</param>
    /// <param name="latencyThreshold">Required (above zero, at most <see cref="MaxLatencyThreshold"/>) for the latency kind and null otherwise.</param>
    public SloDefinition(string id, int targetPpm, TimeSpan window, SloIndicatorKind kind, TimeSpan? latencyThreshold = null)
    {
        if (string.IsNullOrEmpty(id) || !KodoExportPatterns.IdentifierName().IsMatch(id))
        {
            throw new ArgumentException("An SLO id must be an identifier.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(targetPpm, MinTargetPpm);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPpm, MaxTargetPpm);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, MinWindow);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(window, MaxWindow);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == SloIndicatorKind.LatencyThreshold)
        {
            if (latencyThreshold is null || latencyThreshold.Value <= TimeSpan.Zero || latencyThreshold.Value > MaxLatencyThreshold)
            {
                throw new ArgumentException("A latency SLO needs a threshold above zero and at most one minute.", nameof(latencyThreshold));
            }
        }
        else if (latencyThreshold is not null)
        {
            throw new ArgumentException("Only a latency SLO has a threshold.", nameof(latencyThreshold));
        }

        Id = id;
        TargetPpm = targetPpm;
        Window = window;
        Kind = kind;
        LatencyThreshold = latencyThreshold;
    }

    /// <summary>The SLO id.</summary>
    public string Id { get; }

    /// <summary>The target ratio in parts per million (995000 is 99.5 percent).</summary>
    public int TargetPpm { get; }

    /// <summary>The rolling window.</summary>
    public TimeSpan Window { get; }

    /// <summary>The indicator kind.</summary>
    public SloIndicatorKind Kind { get; }

    /// <summary>The latency threshold, for the latency kind only.</summary>
    public TimeSpan? LatencyThreshold { get; }

    /// <summary>The allowed share of not-good units, in parts per million: <c>1000000 - TargetPpm</c>.</summary>
    public int AllowedPpm => 1_000_000 - TargetPpm;
}

/// <summary>The defaults of doc 46 section 12.3 that fit the two indicator kinds. Settings may only tighten the targets (never below their floors).</summary>
public static class SloDefaults
{
    /// <summary>Company server availability, readiness true: 99.5 percent.</summary>
    public static SloDefinition ServerAvailability() =>
        new("company_server_availability", 995_000, SloDefinition.DefaultWindow, SloIndicatorKind.Availability);

    /// <summary>Group chat append latency: 95 percent of appends under 250 ms (the p95 objective).</summary>
    public static SloDefinition GroupChatAppendLatency() =>
        new("group_chat_append_latency", 950_000, SloDefinition.DefaultWindow, SloIndicatorKind.LatencyThreshold, TimeSpan.FromMilliseconds(250));

    /// <summary>Client stream freshness: under 5 seconds for 99 percent of time. The caller samples with <see cref="SloSamples.FromDuration"/>.</summary>
    public static SloDefinition StreamFreshness() =>
        new("client_stream_freshness", 990_000, SloDefinition.DefaultWindow, SloIndicatorKind.Availability);

    /// <summary>The freshness limit for <see cref="StreamFreshness"/> samples.</summary>
    public static readonly TimeSpan StreamFreshnessLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Model lane availability for one provider: 95 percent, per provider since providers are outside the owner's control. The provider id
    /// must itself be an identifier so the SLO id stays one.
    /// </summary>
    public static SloDefinition ModelLaneAvailability(string providerId) =>
        new("model_lane_" + providerId, 950_000, SloDefinition.DefaultWindow, SloIndicatorKind.Availability);
}
