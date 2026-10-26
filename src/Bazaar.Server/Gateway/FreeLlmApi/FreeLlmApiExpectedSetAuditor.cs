using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// Audits a FreeLLMAPI-routed call against the route's expected set. The routing headers name the model that served
/// the call; the SSE body's model field is only the fallback. The model checks reuse <see cref="DelegatedRouteAudit"/>,
/// and a malformed fallback trail also counts as a violation, so the caller fails closed.
/// </summary>
public static class FreeLlmApiExpectedSetAuditor
{
    public const string MalformedTrailViolation = "(malformed fallback trail)";

    /// <summary>
    /// The violations for one call. An empty list means the call stayed inside the expected set. The served model is
    /// <c>X-Routed-Via</c> when present, otherwise <paramref name="bodyServedModel"/>.
    /// </summary>
    public static IReadOnlyList<string> Violations(GwRoute route, FreeLlmApiRouting routing, string? bodyServedModel)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(routing);

        var served = routing.RoutedModel ?? bodyServedModel;
        var violations = new List<string>(DelegatedRouteAudit.Violations(route, served, routing.TrailModels));
        if (routing.TrailMalformed)
        {
            violations.Add(MalformedTrailViolation);
        }

        return violations.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Which expected-set models are absent from the catalogue (<c>Missing</c>) or present but not ready (<c>NotReady</c>).
    /// A null execution status counts as ready. Both lists are distinct, in expected-set order.
    /// </summary>
    public static FreeLlmApiCoverage Coverage(IReadOnlyList<string> expectedSet, IReadOnlyList<FreeLlmApiModel> catalog)
    {
        ArgumentNullException.ThrowIfNull(expectedSet);
        ArgumentNullException.ThrowIfNull(catalog);

        var missing = new List<string>();
        var notReady = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in expectedSet)
        {
            var id = ModelIdOf(entry);
            if (!seen.Add(id))
            {
                continue;
            }

            var row = catalog.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            if (row is null)
            {
                missing.Add(id);
            }
            else if (row.ExecutionStatus is not null
                && !string.Equals(row.ExecutionStatus, FreeLlmApiModel.ReadyStatus, StringComparison.Ordinal))
            {
                notReady.Add(id);
            }
        }

        return new FreeLlmApiCoverage(missing, notReady);
    }

    private static string ModelIdOf(string entry) =>
        ModelRef.TryParse(entry, out var parsed) ? parsed.ModelId : entry;
}

/// <summary>Expected-set models with no catalogue row (<c>Missing</c>) and with a row that is not ready (<c>NotReady</c>).</summary>
public sealed record FreeLlmApiCoverage(IReadOnlyList<string> Missing, IReadOnlyList<string> NotReady);
