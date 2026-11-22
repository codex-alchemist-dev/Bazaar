namespace Bazaar.Server.Gateway.Selection;

/// <summary>Why an endpoint was filtered out before scoring.</summary>
public enum RejectionReason
{
    Excluded,
    MissingCapability,
    ContextTooSmall,
    BreakerOpen,
    QuotaBlocked,
}

public sealed record Rejection(Endpoint Endpoint, RejectionReason Reason);

/// <summary>Each factor's normalised value (0 to 1, higher is better) and the weighted total.</summary>
public sealed record ScoreBreakdown(double Latency, double Reliability, double Headroom, double Cost, double Identity, double Affinity, double Quality, double TierPenalty, double Total);

public sealed record RankedEndpoint(Endpoint Endpoint, ScoreBreakdown Score);

/// <summary>Advice to start <see cref="Secondary"/> if <see cref="Primary"/> has not produced a first event after <see cref="After"/>.</summary>
public sealed record HedgePlan(Endpoint Primary, Endpoint Secondary, TimeSpan After);

/// <summary>
/// The selector's answer. <see cref="Ranking"/> is the fallback chain, best first, and holds only endpoints the caller passed in.
/// <see cref="TraceCode"/> is a short, secret-free code for the usage row, for example <c>balanced/p2c/n3</c>.
/// </summary>
public sealed record SelectionDecision(
    IReadOnlyList<RankedEndpoint> Ranking,
    IReadOnlyList<Rejection> Rejected,
    HedgePlan? Hedge,
    string Profile,
    string TraceCode);

/// <summary>Ranks an agent's allowed endpoints for one call. Pure: it never calls a provider.</summary>
public interface IEndpointSelector
{
    SelectionDecision Rank(IReadOnlyList<Endpoint> allowed, SelectionRequest request);
}

/// <summary>How an attempt ended, for feedback.</summary>
public enum AttemptOutcomeKind
{
    Success,
    /// <summary>Counts against reliability: provider error, timeout, dropped stream.</summary>
    Failure,
    /// <summary>Rate limited or spend capped: a quota fact, not a reliability one. Not counted as failure.</summary>
    Throttled,
    /// <summary>Caller cancelled, or a hedge lost. No verdict.</summary>
    Abandoned,
}

public sealed record AttemptOutcome(AttemptOutcomeKind Kind, TimeSpan? TimeToFirstEvent = null, TimeSpan? Total = null, int OutputTokens = 0);

/// <summary>An in-flight attempt. Dispose without calling <see cref="Complete"/> to abandon it.</summary>
public interface IAttempt : IDisposable
{
    void Complete(AttemptOutcome outcome);
}

/// <summary>What the tracker knows about one endpoint right now.</summary>
public sealed record EndpointStats(
    double Successes,
    double Failures,
    TimeSpan? PeakEwmaLatency,
    TimeSpan? P95Latency,
    int InFlight)
{
    public double Samples => Successes + Failures;
}

/// <summary>Feedback sink and stats source. In memory; a restart starts from priors.</summary>
public interface IEndpointStatsTracker
{
    IAttempt Begin(EndpointKey key, string? affinityKey = null);

    EndpointStats Snapshot(EndpointKey key);

    /// <summary>The endpoint that last served the affinity key, if it is still remembered.</summary>
    EndpointKey? AffinityHolder(string affinityKey);
}

/// <summary>
/// Orders candidates the gateway has already checked against policy. It may reorder and omit; it never adds. The gateway
/// ignores any answer that holds an endpoint it did not pass in.
/// </summary>
public interface ICandidateRanker
{
    /// <summary>The route's candidates in the order to try them, or null to keep the original order (route not bound, or no opinion).</summary>
    IReadOnlyList<Endpoint>? Order(string routeId, IReadOnlyList<Endpoint> candidates, SelectionRequest request);
}

/// <summary>
/// How a route is bound. <c>IdentityTiers</c> makes the endpoint identity a hard tier (doc 05 Claude lane): the preferred identity
/// (<c>native</c> when <c>NativeFirst</c>, else <c>pooled</c>) leads and the other only backs it up, so a pooled endpoint serves
/// when the native one is blocked, failing or quota-limited, not merely when it scores better.
/// </summary>
public sealed record SelectionBinding(string Profile, bool IdentityTiers = false, bool NativeFirst = true);

/// <summary>Used by the first-run wizard and the admin panel to bind a route to a profile (doc 92, wizard contract).</summary>
public interface IAgentSelectionSetup
{
    Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken = default);

    Task BindAsync(string routeId, string profileName, CancellationToken cancellationToken = default);

    Task BindAsync(string routeId, SelectionBinding binding, CancellationToken cancellationToken = default);
}

/// <summary>One endpoint's persisted counters. Times are not stored: counters restart their decay clock on load.</summary>
public sealed record EndpointStatsSnapshot(EndpointKey Key, double Successes, double Failures, double? PeakEwmaMs);

/// <summary>Export and import of the tracker's learned state, so a restart does not start from priors (doc 92 open question 3).</summary>
public interface IPersistentStats
{
    IReadOnlyList<EndpointStatsSnapshot> Export();

    void Import(IEnumerable<EndpointStatsSnapshot> snapshots);
}

/// <summary>One recorded choice, for the admin and client panes. Holds no prompt and no secret.</summary>
public sealed record DecisionRecord(
    DateTimeOffset At,
    string Route,
    string Profile,
    string TraceCode,
    IReadOnlyList<RankedView> Ranking,
    IReadOnlyList<RejectedView> Rejected);

public sealed record RankedView(EndpointKey Key, string Identity, double Score);

public sealed record RejectedView(EndpointKey Key, RejectionReason Reason);

/// <summary>An endpoint's current health as the selector sees it.</summary>
public sealed record EndpointHealthView(EndpointKey Key, double Successes, double Failures, double? PeakEwmaMs, double? P95Ms, int InFlight);

/// <summary>Read side for the admin panel and the client pane: what the selector knows and what it recently chose.</summary>
public interface ISelectionInsights
{
    IReadOnlyList<DecisionRecord> Recent(int count = 50);

    IReadOnlyList<EndpointHealthView> Endpoints();
}
