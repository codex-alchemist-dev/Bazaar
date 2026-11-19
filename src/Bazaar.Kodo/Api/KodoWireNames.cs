using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Api;

/// <summary>
/// The wire strings of the read API and the <c>kodo</c> stream (doc 46 sections 8.1, 9 and 15). The wire never carries
/// C# member names, so renaming an enum member cannot change the JSON.
/// </summary>
public static class KodoWireNames
{
    /// <summary>The wire string of a Kodo health state.</summary>
    public static string State(KodoState state) => state switch
    {
        KodoState.Healthy => "healthy",
        KodoState.Degraded => "degraded",
        KodoState.Failing => "failing",
        KodoState.Recovering => "recovering",
        KodoState.Frozen => "frozen",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a Kodo health state."),
    };

    /// <summary>The display value of a server state, with no staleness applied.</summary>
    public static KodoDisplayState DisplayOf(KodoState state) => state switch
    {
        KodoState.Healthy => KodoDisplayState.Healthy,
        KodoState.Degraded => KodoDisplayState.Degraded,
        KodoState.Failing => KodoDisplayState.Failing,
        KodoState.Recovering => KodoDisplayState.Recovering,
        KodoState.Frozen => KodoDisplayState.Frozen,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a Kodo health state."),
    };

    /// <summary>The wire string of a display value, including <c>unobserved</c>.</summary>
    public static string Display(KodoDisplayState display) => display switch
    {
        KodoDisplayState.Healthy => "healthy",
        KodoDisplayState.Degraded => "degraded",
        KodoDisplayState.Failing => "failing",
        KodoDisplayState.Recovering => "recovering",
        KodoDisplayState.Frozen => "frozen",
        KodoDisplayState.Unobserved => "unobserved",
        _ => throw new ArgumentOutOfRangeException(nameof(display), display, "Not a display value."),
    };

    /// <summary>The wire string of a component's status (doc 46 section 9: unknown never counts as green).</summary>
    public static string Probe(ProbeStatus status) => status switch
    {
        ProbeStatus.Green => "green",
        ProbeStatus.Amber => "amber",
        ProbeStatus.Red => "red",
        ProbeStatus.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a probe status."),
    };
}
