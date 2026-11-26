namespace Bazaar.Engines.Godot.SceneDiff;

/// <summary>
/// Compares two Godot 4 text resources (.tscn or .tres) by node path and property, not by line
/// (docs/design/20-engine-integration-api.md, Godot adapter).
/// </summary>
public static class SceneDiff
{
    /// <summary>
    /// Returns the changes from <paramref name="before"/> to <paramref name="after"/>, sorted by path, then key, ordinal.
    /// Node changes come first, then resource changes. A rename or move is reported as a removal plus an addition.
    /// Throws <see cref="SceneParseException"/> if either file is malformed.
    /// </summary>
    public static IReadOnlyList<SceneChange> Compare(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        ParsedScene previous = GodotTextParser.Parse(before);
        ParsedScene current = GodotTextParser.Parse(after);
        var changes = new List<SceneChange>();

        foreach (ParsedNode node in previous.Nodes.Values)
        {
            if (!current.Nodes.ContainsKey(node.Path))
            {
                changes.Add(new NodeRemoved(node.Path));
            }
        }

        foreach (ParsedNode node in current.Nodes.Values)
        {
            if (!previous.Nodes.TryGetValue(node.Path, out ParsedNode? old))
            {
                changes.Add(new NodeAdded(node.Path, node.Header.GetValueOrDefault("@type")));
                continue;
            }

            changes.AddRange(DiffValues(old.Header, node.Header).Select(d => new PropertyChanged(node.Path, d.Key, d.Before, d.After)));
            changes.AddRange(DiffValues(old.Properties, node.Properties).Select(d => new PropertyChanged(node.Path, d.Key, d.Before, d.After)));
        }

        foreach (ParsedResource resource in previous.Resources.Values)
        {
            if (!current.Resources.ContainsKey((resource.Kind, resource.Id)))
            {
                changes.Add(new ResourceRemoved(resource.Kind, resource.Id));
            }
        }

        foreach (ParsedResource resource in current.Resources.Values)
        {
            if (!previous.Resources.TryGetValue((resource.Kind, resource.Id), out ParsedResource? old))
            {
                changes.Add(new ResourceAdded(resource.Kind, resource.Id));
                continue;
            }

            changes.AddRange(DiffValues(old.Header, resource.Header).Select(d => new ResourceChanged(resource.Kind, resource.Id, d.Key, d.Before, d.After)));
            changes.AddRange(DiffValues(old.Properties, resource.Properties).Select(d => new ResourceChanged(resource.Kind, resource.Id, d.Key, d.Before, d.After)));
        }

        return changes
            .Select(change => (Change: change, Sort: SortKey(change)))
            .OrderBy(x => x.Sort.Group, StringComparer.Ordinal)
            .ThenBy(x => x.Sort.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Sort.Key, StringComparer.Ordinal)
            .ThenBy(x => x.Sort.Rank)
            .Select(x => x.Change)
            .ToList();
    }

    private static IEnumerable<(string Key, string? Before, string? After)> DiffValues(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        foreach (string key in before.Keys.Union(after.Keys, StringComparer.Ordinal))
        {
            before.TryGetValue(key, out string? beforeValue);
            after.TryGetValue(key, out string? afterValue);
            if (!string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
            {
                yield return (key, beforeValue, afterValue);
            }
        }
    }

    private static (string Group, string Name, string Key, int Rank) SortKey(SceneChange change) => change switch
    {
        NodeAdded c => ("node", c.Path, string.Empty, 0),
        NodeRemoved c => ("node", c.Path, string.Empty, 1),
        PropertyChanged c => ("node", c.Path, c.Key, 2),
        ResourceAdded c => (c.Kind, c.Id, string.Empty, 0),
        ResourceRemoved c => (c.Kind, c.Id, string.Empty, 1),
        ResourceChanged c => (c.Kind, c.Id, c.Key, 2),
        _ => throw new InvalidOperationException($"Unknown scene change type {change.GetType().Name}."),
    };
}
