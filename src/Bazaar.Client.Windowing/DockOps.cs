namespace Bazaar.Client.Windowing;

/// <summary>
/// Pure operations on dock layouts. Every method returns new immutable trees, never mutates its input,
/// and treats an unknown pane id as a no-op rather than an error.
/// </summary>
public static class DockOps
{
    /// <summary>Deepest layout allowed. Operations that would exceed it return the layout unchanged.</summary>
    public const int MaxDepth = 16;

    /// <summary>Lowest split ratio after clamping.</summary>
    public const double MinRatio = 0.1;

    /// <summary>Highest split ratio after clamping.</summary>
    public const double MaxRatio = 0.9;

    /// <summary>Every pane leaf in document order (first child before second, tabs left to right).</summary>
    public static IReadOnlyList<DockPane> Panes(DockNode? root)
    {
        var result = new List<DockPane>();
        CollectPanes(root, result);
        return result;
    }

    /// <summary>The pane with <paramref name="paneId"/>, or null when it is not in the layout.</summary>
    public static DockPane? Find(DockNode? root, string paneId)
    {
        foreach (var pane in Panes(root))
        {
            if (pane.PaneId == paneId)
            {
                return pane;
            }
        }

        return null;
    }

    /// <summary>
    /// Removes a pane. A tab group keeps at least one pane and stays a <see cref="DockTabs"/>; an empty group is
    /// removed and its parent split collapses to the surviving sibling. An unknown id returns the layout unchanged.
    /// </summary>
    public static DockDetachResult Detach(DockNode? root, string paneId)
    {
        if (root is null)
        {
            return new DockDetachResult(null, null);
        }

        var remaining = RemoveFrom(root, paneId, out var removed);
        return new DockDetachResult(remaining, removed);
    }

    /// <summary>
    /// Places <paramref name="pane"/> relative to the pane <paramref name="targetPaneId"/>. Center adds it as the
    /// last tab of the target's group and activates it. The four edges wrap the target's whole tab group in a
    /// 0.5 split. A null root yields a single tab group holding the pane. An unknown target, a duplicate pane id,
    /// or a result deeper than <see cref="MaxDepth"/> returns the root unchanged.
    /// </summary>
    public static DockNode Dock(DockNode? root, DockPane pane, string targetPaneId, DockEdge edge)
    {
        if (root is null)
        {
            return new DockTabs(new List<DockPane> { pane }, 0);
        }

        if (!Enum.IsDefined(edge)
            || Find(root, pane.PaneId) is not null
            || Find(root, targetPaneId) is null)
        {
            return root;
        }

        var docked = Replace(root, targetPaneId, group => edge == DockEdge.Center
            ? AppendTab(group, pane)
            : WrapGroup(group, pane, edge));

        return Depth(docked) > MaxDepth ? root : docked;
    }

    /// <summary>Makes <paramref name="paneId"/> the active tab of its group. A bare pane or unknown id is left as is.</summary>
    public static DockNode? ActivateTab(DockNode? root, string paneId)
    {
        if (root is null || Find(root, paneId) is null)
        {
            return root;
        }

        return Replace(root, paneId, group => group is DockTabs tabs
            ? tabs with { ActiveIndex = IndexOf(tabs.Tabs, paneId) }
            : group);
    }

    /// <summary>
    /// Moves a tab within its group to <paramref name="newIndex"/> (clamped to the group). The moved pane stays
    /// active. A bare pane or unknown id is left as is.
    /// </summary>
    public static DockNode? MoveTab(DockNode? root, string paneId, int newIndex)
    {
        if (root is null || Find(root, paneId) is null)
        {
            return root;
        }

        return Replace(root, paneId, group =>
        {
            if (group is not DockTabs tabs)
            {
                return group;
            }

            var tabList = new List<DockPane>(tabs.Tabs);
            var from = IndexOf(tabList, paneId);
            var moving = tabList[from];
            tabList.RemoveAt(from);
            var to = Math.Clamp(newIndex, 0, tabList.Count);
            tabList.Insert(to, moving);
            return new DockTabs(tabList, to);
        });
    }

    /// <summary>Clamps a split ratio into [<see cref="MinRatio"/>, <see cref="MaxRatio"/>]. NaN becomes 0.5.</summary>
    internal static double ClampRatio(double ratio)
    {
        return double.IsNaN(ratio) ? 0.5 : Math.Clamp(ratio, MinRatio, MaxRatio);
    }

    /// <summary>Nesting depth: a pane or tab group is 1, a split is one more than its deeper child.</summary>
    internal static int Depth(DockNode? node)
    {
        return node switch
        {
            null => 0,
            DockSplit split => 1 + Math.Max(Depth(split.First), Depth(split.Second)),
            _ => 1,
        };
    }

    private static void CollectPanes(DockNode? node, List<DockPane> into)
    {
        switch (node)
        {
            case DockPane pane:
                into.Add(pane);
                break;
            case DockTabs tabs:
                into.AddRange(tabs.Tabs);
                break;
            case DockSplit split:
                CollectPanes(split.First, into);
                CollectPanes(split.Second, into);
                break;
        }
    }

    private static DockNode? RemoveFrom(DockNode node, string paneId, out DockPane? removed)
    {
        switch (node)
        {
            case DockPane pane when pane.PaneId == paneId:
                removed = pane;
                return null;

            case DockTabs tabs:
                {
                    var index = IndexOf(tabs.Tabs, paneId);
                    if (index < 0)
                    {
                        removed = null;
                        return node;
                    }

                    removed = tabs.Tabs[index];
                    if (tabs.Tabs.Count == 1)
                    {
                        return null;
                    }

                    var rest = new List<DockPane>(tabs.Tabs);
                    rest.RemoveAt(index);
                    var active = tabs.ActiveIndex;
                    if (index < active)
                    {
                        active--;
                    }

                    return new DockTabs(rest, Math.Clamp(active, 0, rest.Count - 1));
                }

            case DockSplit split:
                {
                    var first = RemoveFrom(split.First, paneId, out var removedFirst);
                    if (removedFirst is not null)
                    {
                        removed = removedFirst;
                        return first is null
                            ? split.Second
                            : new DockSplit(split.Orientation, ClampRatio(split.Ratio), first, split.Second);
                    }

                    var second = RemoveFrom(split.Second, paneId, out removed);
                    if (removed is null)
                    {
                        return split;
                    }

                    return second is null
                        ? split.First
                        : new DockSplit(split.Orientation, ClampRatio(split.Ratio), split.First, second);
                }

            default:
                removed = null;
                return node;
        }
    }

    /// <summary>Rewrites the tab group (or bare pane) that holds <paramref name="targetPaneId"/>.</summary>
    private static DockNode Replace(DockNode node, string targetPaneId, Func<DockNode, DockNode> rewrite)
    {
        switch (node)
        {
            case DockPane pane when pane.PaneId == targetPaneId:
                return rewrite(pane);

            case DockTabs tabs when IndexOf(tabs.Tabs, targetPaneId) >= 0:
                return rewrite(tabs);

            case DockSplit split:
                return Find(split.First, targetPaneId) is not null
                    ? new DockSplit(split.Orientation, split.Ratio, Replace(split.First, targetPaneId, rewrite), split.Second)
                    : new DockSplit(split.Orientation, split.Ratio, split.First, Replace(split.Second, targetPaneId, rewrite));

            default:
                return node;
        }
    }

    private static DockTabs AppendTab(DockNode group, DockPane pane)
    {
        var tabs = group switch
        {
            DockPane single => new List<DockPane> { single },
            DockTabs existing => new List<DockPane>(existing.Tabs),
            _ => throw new InvalidOperationException("Tab target must be a pane or tab group."),
        };

        tabs.Add(pane);
        return new DockTabs(tabs, tabs.Count - 1);
    }

    private static DockSplit WrapGroup(DockNode group, DockPane pane, DockEdge edge)
    {
        return edge switch
        {
            DockEdge.Left => new DockSplit(DockOrientation.Horizontal, 0.5, pane, group),
            DockEdge.Right => new DockSplit(DockOrientation.Horizontal, 0.5, group, pane),
            DockEdge.Top => new DockSplit(DockOrientation.Vertical, 0.5, pane, group),
            DockEdge.Bottom => new DockSplit(DockOrientation.Vertical, 0.5, group, pane),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Not a split edge."),
        };
    }

    private static int IndexOf(IReadOnlyList<DockPane> tabs, string paneId)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].PaneId == paneId)
            {
                return i;
            }
        }

        return -1;
    }
}
