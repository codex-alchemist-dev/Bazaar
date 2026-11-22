using Bazaar.Server.Gateway.Data;

namespace Bazaar.Server.Gateway.Policy;

/// <summary>
/// After-the-call check for delegated (<c>custom/*</c>) routes, where the upstream picks the model and Bazaar cannot
/// block a wrong pick beforehand (doc 05). A served model, or any hop in the fallback trail, outside the route's
/// expected set is a violation; the caller suspends the route and raises an admin alert.
/// </summary>
public static class DelegatedRouteAudit
{
    public static IReadOnlyList<string> Violations(GwRoute route, string? servedModel, IEnumerable<string>? fallbackTrail = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        var seen = new List<string>();
        if (servedModel is not null)
        {
            seen.Add(servedModel);
        }

        if (fallbackTrail is not null)
        {
            seen.AddRange(fallbackTrail);
        }

        var violations = seen.Where(model => !InExpectedSet(route, model)).Distinct(StringComparer.Ordinal).ToList();
        if (servedModel is null)
        {
            violations.Add("(no served model reported)");
        }

        return violations;
    }

    private static bool InExpectedSet(GwRoute route, string model) =>
        route.ExpectedSet.Any(e => string.Equals(e, model, StringComparison.Ordinal)
            || (ModelRef.TryParse(e, out var expected) && string.Equals(expected.ModelId, model, StringComparison.Ordinal)));
}
