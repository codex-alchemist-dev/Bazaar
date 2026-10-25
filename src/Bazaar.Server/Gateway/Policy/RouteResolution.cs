namespace Bazaar.Server.Gateway.Policy;

/// <summary>Why a request or a single candidate was refused.</summary>
public enum DenyReason
{
    UnknownAgent,
    InvalidRoute,
    NoRoute,
    ModelNotAllowed,
    RouteNotAllowed,
    UnknownRoute,
    ToolsNotAllowed,
    DelegatedSetExceedsPolicy,
    CredentialRefused,
    ProviderUnavailable,
    ModelUnavailable,
    DeniedByTightening,
    FamilyMismatch,
    NoEligibleCandidate,
}

/// <summary>One model the gateway may call for a request. A delegated candidate names an upstream profile, not a model.</summary>
public sealed record RouteCandidate(ModelRef Model, string Family, bool Delegated);

/// <summary>A candidate that was dropped and why, for the admin panel and tests.</summary>
public sealed record DroppedCandidate(ModelRef Model, DenyReason Reason);

/// <summary>The outcome of resolving a request against policy.</summary>
public abstract record RouteResolution
{
    /// <summary>The request may proceed with these candidates, in order. Never empty.</summary>
    public sealed record Allowed(ResolvedRoute Route, IReadOnlyList<DroppedCandidate> Dropped) : RouteResolution;

    /// <summary>The request is refused. Nothing was called.</summary>
    public sealed record Denied(DenyReason Reason, string Detail, IReadOnlyList<DroppedCandidate> Dropped) : RouteResolution;
}

/// <summary>
/// The candidates a request may use, fixed at resolve time (policy changes apply to the next call; in-flight calls
/// finish). Every attempt, including a fallback, must come from this list: <see cref="Next"/> is the only way to pick one.
/// </summary>
public sealed class ResolvedRoute
{
    internal ResolvedRoute(
        string agentId,
        string route,
        Data.RouteStrategy strategy,
        long policyVersion,
        IReadOnlyList<RouteCandidate> candidates,
        IReadOnlyList<string> expectedSet)
    {
        AgentId = agentId;
        Route = route;
        Strategy = strategy;
        PolicyVersion = policyVersion;
        Candidates = candidates;
        ExpectedSet = expectedSet;
    }

    public string AgentId { get; }

    public string Route { get; }

    public Data.RouteStrategy Strategy { get; }

    public long PolicyVersion { get; }

    /// <summary>Allowed candidates in priority order. Never empty.</summary>
    public IReadOnlyList<RouteCandidate> Candidates { get; }

    /// <summary>For a delegated route: the models the upstream is expected to serve. Empty otherwise.</summary>
    public IReadOnlyList<string> ExpectedSet { get; }

    public bool IsDelegated => Candidates.Any(c => c.Delegated);

    /// <summary>
    /// The next candidate that has not been tried, or null when none is left. A fallback never reaches a model that
    /// is not in <see cref="Candidates"/>, and a model in <paramref name="excluded"/> is skipped.
    /// </summary>
    public RouteCandidate? Next(IReadOnlySet<ModelRef> tried, IReadOnlySet<ModelRef>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(tried);
        foreach (var candidate in Candidates)
        {
            if (!tried.Contains(candidate.Model) && excluded?.Contains(candidate.Model) != true)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>True when the model is one of this request's candidates. Adapters check this before every attempt.</summary>
    public bool Authorizes(ModelRef model) => Candidates.Any(c => c.Model == model);
}
