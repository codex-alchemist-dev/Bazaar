namespace Bazaar.Server.Components;

/// <summary>Validates and orders component manifests (docs/design/BAZAAR-COMPONENTS.md section 3, steps 2 and 3).</summary>
public static class ComponentGraph
{
    /// <summary>
    /// Checks ids, self and unknown dependencies, and cycles. Returns the manifests in dependency order, ties broken by id,
    /// so the order is the same on every start.
    /// </summary>
    /// <exception cref="ComponentGraphException">The graph cannot start. The message names the components involved.</exception>
    public static IReadOnlyList<ComponentManifest> Order(IEnumerable<ComponentManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        var byId = new Dictionary<string, ComponentManifest>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            if (!byId.TryAdd(manifest.Id, manifest))
            {
                throw new ComponentGraphException($"Duplicate component id '{manifest.Id}'.");
            }
        }

        foreach (var manifest in byId.Values)
        {
            foreach (var dependency in manifest.DependsOn)
            {
                if (dependency == manifest.Id)
                {
                    throw new ComponentGraphException($"Component '{manifest.Id}' depends on itself.");
                }

                if (!byId.ContainsKey(dependency))
                {
                    throw new ComponentGraphException($"Component '{manifest.Id}' depends on unknown component '{dependency}'.");
                }
            }
        }

        // Kahn's algorithm over ids sorted ordinally, so ties resolve the same way on every start.
        var remaining = byId.Keys.Order(StringComparer.Ordinal).ToList();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ComponentManifest>(byId.Count);

        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(id => byId[id].DependsOn.All(placed.Contains));
            if (ready is null)
            {
                throw new ComponentGraphException(
                    "Component dependency cycle among: " + string.Join(", ", remaining.Order(StringComparer.Ordinal)) + ".");
            }

            remaining.Remove(ready);
            placed.Add(ready);
            ordered.Add(byId[ready]);
        }

        return ordered;
    }
}
