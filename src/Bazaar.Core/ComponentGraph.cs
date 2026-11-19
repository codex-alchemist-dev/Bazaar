using System.Text.RegularExpressions;

namespace Bazaar.Core;

/// <summary>Validates and orders component manifests (docs/design/BAZAAR-COMPONENTS.md section 3, steps 2 and 3).</summary>
public static partial class ComponentGraph
{
    private const int _maxIdLength = 64;

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
            CheckShape(manifest);
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

    // Ids are lowercase kebab-case (ComponentManifest.MigrationNamespace relies on it) and the lists are never null:
    // a bad manifest is a graph error that names the component, not a NullReferenceException or a path-shaped id.
    private static void CheckShape(ComponentManifest manifest)
    {
        if (manifest.Id is null || manifest.Id.Length > _maxIdLength || !IdPattern().IsMatch(manifest.Id))
        {
            throw new ComponentGraphException($"Component id '{manifest.Id}' is not lowercase kebab-case of up to {_maxIdLength} characters.");
        }

        if (manifest.DependsOn is null || manifest.DependsOn.Any(d => d is null) || manifest.Provides is null
            || (manifest.SoftDependsOn?.Any(d => d is null) ?? false))
        {
            throw new ComponentGraphException($"Component '{manifest.Id}' has a null dependency list or entry.");
        }
    }

    // \z, not $: in .NET '$' also matches before a trailing newline.
    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
