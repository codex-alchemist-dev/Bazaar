namespace Bazaar.Core;

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

            foreach (var soft in manifest.Soft)
            {
                if (soft == manifest.Id)
                {
                    throw new ComponentGraphException($"Component '{manifest.Id}' softly depends on itself.");
                }
            }
        }

        // Only hard edges set start order (docs/design/BAZAAR-COMPONENTS.md section 4.1, owner 2026-11-16). Soft edges are feature
        // links resolved from metadata, so they may form cycles and never reorder components.
        bool Ready(string id, HashSet<string> placedIds) => byId[id].DependsOn.All(placedIds.Contains);

        // Kahn's algorithm over ids sorted ordinally, so ties resolve the same way on every start.
        var remaining = byId.Keys.Order(StringComparer.Ordinal).ToList();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ComponentManifest>(byId.Count);

        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(id => Ready(id, placed));
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

    /// <summary>
    /// Works out what a failed component blocks and degrades. Only hard edges block, and they block transitively. A soft edge never
    /// blocks: a component that only softly depends on the failed one, or on anything it blocked, is degraded.
    /// </summary>
    /// <exception cref="ComponentGraphException">The graph is invalid or <paramref name="failedId"/> is not in it.</exception>
    public static ComponentImpact Impact(IEnumerable<ComponentManifest> manifests, string failedId)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentException.ThrowIfNullOrEmpty(failedId);

        var all = Order(manifests);
        if (all.All(m => m.Id != failedId))
        {
            throw new ComponentGraphException($"Unknown component '{failedId}'.");
        }

        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var down = new HashSet<string>(StringComparer.Ordinal) { failedId };

        // Order is dependency order, so one pass sees every hard target before its dependents.
        foreach (var manifest in all)
        {
            if (manifest.DependsOn.Any(down.Contains))
            {
                blocked.Add(manifest.Id);
                down.Add(manifest.Id);
            }
        }

        var degraded = all
            .Where(m => !down.Contains(m.Id) && m.Soft.Any(down.Contains))
            .Select(m => m.Id)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new ComponentImpact(failedId, blocked.Order(StringComparer.Ordinal).ToList(), degraded);
    }
}
