namespace Bazaar.Constellation.Rendering.Core;

/// <summary>Limits for one frame's tile selection. Tighten them, never loosen below the doc 40 floors.</summary>
/// <param name="MaxInstances">Hard cap on instances the selection may ask the GPU to draw.</param>
/// <param name="RefineAtPixels">A tile smaller than this on screen is drawn from its sample, not split.</param>
/// <param name="MaxTiles">Hard cap on tiles in the result.</param>
public sealed record ViewSelectorOptions(long MaxInstances = 2_000_000, float RefineAtPixels = 96f, int MaxTiles = 8192);

/// <summary>One tile chosen for this frame and how many instances drawing it costs.</summary>
public readonly record struct SelectedTile(TileKey Key, long Instances, bool Full);

public sealed record ViewSelection(IReadOnlyList<SelectedTile> Tiles, long TotalInstances, int TilesVisited);

/// <summary>
/// CPU reference for the per-frame tile choice (docs/design/constellation-scale-and-lod.md sections 4 and 5).
/// Starts at the root and always splits the biggest on-screen tile next, until the instance cap, the tile cap, or the
/// pixel threshold stops it. Work and result size depend on the caps and the view, never on the node count.
/// The GPU compute pass is checked against this.
/// </summary>
public static class ViewSelector
{
    public static ViewSelection Select(ITileTree tree, ViewVolume view, ViewSelectorOptions? options = null)
    /// <param name="isOccluded">
    /// Optional occlusion test (for example <see cref="OcclusionCuller.AsTest"/> from last frame's Hi-Z pyramid). Tiles it
    /// rejects are skipped like off-screen tiles, so they never consume the instance or tile budget. It must be conservative.
    /// </param>
    public static ViewSelection Select(ITileTree tree, ViewVolume view, ViewSelectorOptions? options = null, Func<TileBounds, bool>? isOccluded = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(view);
        options ??= new ViewSelectorOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxInstances);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxTiles);

        var done = new List<SelectedTile>();
        var open = new PriorityQueue<(TileKey Key, TileInfo Info, long Cost), float>();
        long total = 0;
        int visited = 0;
        TileKey root = TileKey.Root;
        TileInfo rootInfo = tree.GetInfo(root);
        visited++;
        if (!view.Intersects(rootInfo.Bounds))
        if (!view.Intersects(rootInfo.Bounds) || (isOccluded?.Invoke(rootInfo.Bounds) ?? false))
        {
            return new ViewSelection(done, 0, visited);
        }

        long rootCost = Math.Min(Cost(rootInfo), options.MaxInstances);
        total += rootCost;
        open.Enqueue((root, rootInfo, rootCost), -view.ProjectedPixels(rootInfo.Bounds));
        Span<TileKey> kids = stackalloc TileKey[8];
        Span<(TileKey Key, TileInfo Info, long Cost)> visible = stackalloc (TileKey, TileInfo, long)[8];

        while (open.TryDequeue(out var tile, out float negPixels))
        {
            float pixels = -negPixels;
            if (!tile.Info.IsLeaf && pixels >= options.RefineAtPixels)
            {
                int n = tree.GetChildren(tile.Key, kids);
                int v = 0;
                long childCost = 0;
                for (int i = 0; i < n; i++)
                {
                    TileInfo info = tree.GetInfo(kids[i]);
                    visited++;
                    if (!view.Intersects(info.Bounds))
                    if (!view.Intersects(info.Bounds) || (isOccluded?.Invoke(info.Bounds) ?? false))
                    {
                        continue;
                    }

                    long cost = Cost(info);
                    visible[v++] = (kids[i], info, cost);
                    childCost += cost;
                }

                int tilesAfter = done.Count + open.Count + v;
                if (v > 0 && total - tile.Cost + childCost <= options.MaxInstances && tilesAfter <= options.MaxTiles)
                {
                    total += childCost - tile.Cost;
                    for (int i = 0; i < v; i++)
                    {
                        open.Enqueue(visible[i], -view.ProjectedPixels(visible[i].Info.Bounds));
                    }

                    continue;
                }

                if (v == 0)
                {
                    // Nothing of this tile is on screen after all (box test is conservative); drop it.
                    total -= tile.Cost;
                    continue;
                }
            }

            done.Add(new SelectedTile(tile.Key, tile.Cost, tile.Info.IsLeaf));
        }

        return new ViewSelection(done, total, visited);
    }

    private static long Cost(TileInfo info) => info.IsLeaf ? info.NodeCount : info.SampleCount;
}
