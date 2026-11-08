namespace Bazaar.Client.Core.Constellation;

/// <summary>One visible node as the list sees it: id, screen-reader label and kind.</summary>
public sealed record AccessibleNodeInput(string NodeId, string Label, string Kind);

/// <summary>One row of the accessible list. <see cref="Index"/> is the position in the ordered list.</summary>
public sealed record AccessibleNodeItem(int Index, string NodeId, string Label, string Kind, bool IsFocused);

/// <summary>
/// Ordered, keyboard-navigable mirror of the visible Constellation nodes, for screen readers and keyboard-only use (19, WP10).
/// Pure model: no Avalonia. Each instance is a snapshot. Build a new one when the nodes or the focus change.
/// Default order is by kind, then by label, both ordinal. Items with equal kind and label keep their input order.
/// </summary>
public sealed class AccessibleNodeList
{
    private readonly IReadOnlyList<AccessibleNodeItem> _items;

    private AccessibleNodeList(IReadOnlyList<AccessibleNodeItem> items, int focusedIndex)
    {
        _items = items;
        FocusedIndex = focusedIndex;
    }

    /// <summary>Ordered items.</summary>
    public IReadOnlyList<AccessibleNodeItem> Items => _items;

    /// <summary>Number of items.</summary>
    public int Count => _items.Count;

    /// <summary>Index of the focused item, or -1 when the focused node is not in the list (or nothing is focused).</summary>
    public int FocusedIndex { get; }

    /// <summary>
    /// Builds the list. <paramref name="focusedNodeId"/> may be null. Throws <see cref="ArgumentException"/> when a node id
    /// appears twice, because the list cannot tell two rows with the same id apart.
    /// </summary>
    public static AccessibleNodeList Create(IEnumerable<AccessibleNodeInput> nodes, string? focusedNodeId)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sorted = new List<AccessibleNodeInput>();
        foreach (AccessibleNodeInput node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            ArgumentNullException.ThrowIfNull(node.NodeId);
            ArgumentNullException.ThrowIfNull(node.Label);
            ArgumentNullException.ThrowIfNull(node.Kind);
            if (!seen.Add(node.NodeId))
            {
                throw new ArgumentException($"Duplicate node id '{node.NodeId}' in the accessible list.", nameof(nodes));
            }

            sorted.Add(node);
        }

        // OrderBy is stable, so ties keep the input order.
        List<AccessibleNodeInput> ordered = sorted
            .OrderBy(n => n.Kind, StringComparer.Ordinal)
            .ThenBy(n => n.Label, StringComparer.Ordinal)
            .ToList();

        var items = new List<AccessibleNodeItem>(ordered.Count);
        int focusedIndex = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            AccessibleNodeInput node = ordered[i];
            bool isFocused = focusedNodeId is not null && node.NodeId == focusedNodeId;
            if (isFocused)
            {
                focusedIndex = i;
            }

            items.Add(new AccessibleNodeItem(i, node.NodeId, node.Label, node.Kind, isFocused));
        }

        return new AccessibleNodeList(items, focusedIndex);
    }

    /// <summary>
    /// Index after <paramref name="current"/>, wrapping from the last item to the first. A current index of -1 (no
    /// selection) gives the first item. Returns -1 for an empty list.
    /// </summary>
    public int NextIndex(int current)
    {
        if (_items.Count == 0)
        {
            return -1;
        }

        if (current < 0)
        {
            return 0;
        }

        return (current + 1) % _items.Count;
    }

    /// <summary>
    /// Index before <paramref name="current"/>, wrapping from the first item to the last. A current index of -1 gives the
    /// last item. Returns -1 for an empty list.
    /// </summary>
    public int PreviousIndex(int current)
    {
        if (_items.Count == 0)
        {
            return -1;
        }

        if (current < 0)
        {
            return _items.Count - 1;
        }

        return (current - 1 + _items.Count) % _items.Count;
    }
}
