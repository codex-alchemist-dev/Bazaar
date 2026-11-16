using Bazaar.Core;
using Bazaar.CompanyBuild.Abstractions;

namespace Bazaar.CompanyBuild;

/// <summary>
/// Composes a company's build from its selected modules through Bazaar.Core's graph (docs/design/BAZAAR-COMPONENTS.md section 4.1).
/// Hard dependencies are added and must be acyclic. Soft dependencies are kept only when their module is in the plan, and never
/// set start order.
/// </summary>
public static class CompanyBuildComposer
{
    /// <summary>Builds the plan for one company.</summary>
    /// <param name="catalogue">Every module known to the server build.</param>
    /// <param name="request">The company and its selection.</param>
    /// <exception cref="CompanyBuildException">A selected module is unknown, or the catalogue lacks a hard dependency.</exception>
    /// <exception cref="ComponentGraphException">The selected modules' hard edges form a cycle or are otherwise invalid.</exception>
    public static CompanyBuildPlan Compose(IEnumerable<ComponentManifest> catalogue, CompanyBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CompanyId);

        // ComponentGraph.Order also rejects duplicate ids and unknown or self dependencies across the whole catalogue.
        var known = ComponentGraph.Order(catalogue).ToDictionary(m => m.Id, StringComparer.Ordinal);

        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in request.SelectedModuleIds)
        {
            if (!known.ContainsKey(id))
            {
                throw new CompanyBuildException($"Company '{request.CompanyId}' selected unknown module '{id}'.");
            }
            selected.Add(id);
        }

        // Close over hard edges only. Soft edges never pull a module in.
        var included = new HashSet<string>(selected, StringComparer.Ordinal);
        var stack = new Stack<string>(selected);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var dependency in known[current].DependsOn)
            {
                if (included.Add(dependency))
                {
                    stack.Push(dependency);
                }
            }
        }

        var narrowed = included
            .Order(StringComparer.Ordinal)
            .Select(id => known[id] with
            {
                SoftDependsOn = known[id].Soft.Where(included.Contains).ToList(),
            })
            .ToList();

        // Validates that the closed set's hard edges are acyclic and gives the start order.
        var ordered = ComponentGraph.Order(narrowed);

        var added = included.Except(selected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new CompanyBuildPlan(request.CompanyId, ordered, added);
    }
}
