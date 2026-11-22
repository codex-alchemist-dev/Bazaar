namespace Bazaar.Constellation.Rendering.Core;

/// <summary>How one selected tile is drawn this frame.</summary>
public enum TileDrawMode
{
    /// <summary>Individual node instances, optionally decimated to <see cref="TileDraw.NodeCount"/>.</summary>
    Nodes,

    /// <summary>One write into the density field; the tile's nodes are below a pixel each (doc section 11, fully zoomed out).</summary>
    Splat,
}

/// <summary>One tile's draw decision. <see cref="Available"/> is what the selector priced it at; <see cref="NodeCount"/> is what is drawn.</summary>
public readonly record struct TileDraw(TileKey Key, TileDrawMode Mode, long Available, long NodeCount, int Edges);

/// <summary>Frame totals of a plan. <see cref="NodeInstances"/> never exceeds the instance budget, <see cref="Edges"/> never the edge budget.</summary>
public readonly record struct TileDrawPlan(long NodeInstances, int Edges, int NodeTiles, int SplatTiles);

/// <param name="SplatBelowPixelsPerNode">A tile whose projected screen area per real node (not per sampled instance) is under this draws as a density splat.</param>
public sealed record TileDrawOptions(float SplatBelowPixelsPerNode = 1f);

/// <summary>
/// Turns a <see cref="ViewSelection"/> and the frame budget into per-tile draw decisions (docs/design/constellation-scale-and-lod.md
/// section 11): density splat for tiles under one pixel per node, otherwise node instances, decimated by the same ratio in every
/// tile when the total passes the instance cap, and the edge budget split by on-screen size across tiles drawn at full detail.
/// Pure and deterministic; work depends on the tile count, never on the node count.
/// </summary>
public static class TileDrawPlanner
{
    /// <summary>Plans with the controller's current caps. <paramref name="output"/> is cleared and reused.</summary>
    public static TileDrawPlan Plan(ViewSelection selection, ITileTree tree, ViewVolume view, FrameBudgetController budget, List<TileDraw> output, TileDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return Plan(selection, tree, view, budget.Current().MaxInstances, budget.EdgeBudget, output, options);
    }

    public static TileDrawPlan Plan(ViewSelection selection, ITileTree tree, ViewVolume view, long maxInstances, int edgeBudget, List<TileDraw> output, TileDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maxInstances);
        ArgumentOutOfRangeException.ThrowIfNegative(edgeBudget);
        options ??= new TileDrawOptions();

        output.Clear();
        IReadOnlyList<SelectedTile> tiles = selection.Tiles;
        int n = tiles.Count;

        // Pass 1: splat or nodes, and the total node demand.
        long demand = 0;
        for (int i = 0; i < n; i++)
        {
            SelectedTile t = tiles[i];
            TileInfo info = tree.GetInfo(t.Key);
            float pixels = view.ProjectedPixels(info.Bounds);

            // Judge by the tile's real node count: a sampled tile whose nodes are all sub-pixel reads as density, not dots.
            bool splat = t.Instances > 0 && (double)pixels * pixels < options.SplatBelowPixelsPerNode * (double)Math.Max(info.NodeCount, t.Instances);
            output.Add(new TileDraw(t.Key, splat ? TileDrawMode.Splat : TileDrawMode.Nodes, t.Instances, splat ? 0 : t.Instances, 0));
            if (!splat)
            {
                demand += t.Instances;
            }
        }

        // Pass 2: decimate to the cap with one ratio for all tiles; leftovers go to the earliest tiles still short.
        if (demand > maxInstances)
        {
            long given = 0;
            for (int i = 0; i < n; i++)
            {
                TileDraw d = output[i];
                if (d.Mode != TileDrawMode.Nodes)
                {
                    continue;
                }

                long share = (long)Math.Floor((double)maxInstances * d.Available / demand);
                share = Math.Min(Math.Min(share, d.Available), maxInstances - given);
                given += share;
                output[i] = d with { NodeCount = share };
            }

            for (int i = 0; i < n && given < maxInstances; i++)
            {
                TileDraw d = output[i];
                if (d.Mode == TileDrawMode.Nodes && d.NodeCount < d.Available)
                {
                    output[i] = d with { NodeCount = d.NodeCount + 1 };
                    given++;
                }
            }
        }

        // A tile decimated to nothing has no nodes to show; its density still counts.
        long nodeTotal = 0;
        int nodeTiles = 0;
        int splatTiles = 0;
        double weightSum = 0;
        for (int i = 0; i < n; i++)
        {
            TileDraw d = output[i];
            if (d.Mode == TileDrawMode.Nodes && d.NodeCount == 0 && d.Available > 0)
            {
                d = d with { Mode = TileDrawMode.Splat };
                output[i] = d;
            }

            if (d.Mode == TileDrawMode.Splat)
            {
                splatTiles++;
                continue;
            }

            nodeTiles++;
            nodeTotal += d.NodeCount;
            if (tiles[i].Full)
            {
                weightSum += EdgeWeight(tree, view, d.Key);
            }
        }

        // Pass 3: edges only for tiles at full detail (near); the rest use cluster bundles, outside this budget.
        int edgeTotal = 0;
        if (edgeBudget > 0 && weightSum > 0)
        {
            for (int i = 0; i < n; i++)
            {
                TileDraw d = output[i];
                if (d.Mode != TileDrawMode.Nodes || !tiles[i].Full)
                {
                    continue;
                }

                int e = (int)Math.Min(Math.Floor(edgeBudget * (EdgeWeight(tree, view, d.Key) / weightSum)), edgeBudget - edgeTotal);
                edgeTotal += e;
                output[i] = d with { Edges = e };
            }
        }

        return new TileDrawPlan(nodeTotal, edgeTotal, nodeTiles, splatTiles);
    }

    /// <summary>The indices (ascending) of the nodes to draw for a decimated tile: the top <see cref="TileDraw.NodeCount"/> by importance.</summary>
    public static int[] SelectNodes(in TileDraw draw, ReadOnlySpan<float> importance) =>
        draw.Mode == TileDrawMode.Nodes ? ImportanceRanker.SelectTop(importance, (int)Math.Min(draw.NodeCount, int.MaxValue)) : [];

    // Bigger on screen means more important; clamped so a tile around the camera does not starve the rest.
    private static double EdgeWeight(ITileTree tree, ViewVolume view, in TileKey key) =>
        Math.Clamp(view.ProjectedPixels(tree.GetInfo(key).Bounds), 1f, 100_000f);
}
