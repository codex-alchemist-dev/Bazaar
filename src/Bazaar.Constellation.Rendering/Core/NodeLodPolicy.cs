namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Pure policy that picks the detail rung for a node from its projected pixel size and kind. Cutoffs are screen sizes in
/// pixels at which a rung gives way to the next; they are divided by the live detail scale from
/// <see cref="FrameBudgetController.Detail"/>, so when over budget nodes simplify sooner and with headroom they refine
/// later (same rule as <see cref="RenderTier.RefineAtPixels"/>). Hysteresis keeps a node from flickering between rungs.
/// Doc: docs/design/constellation-node-detail-and-pacing.md section 3 (cutoff numbers PROPOSED).
/// </summary>
public static class NodeLodPolicy
{
    /// <summary>Fraction of a cutoff a size must pass to change rung: finer needs size >= cutoff*(1+h), coarser needs size &lt; cutoff*(1-h).</summary>
    public const float Hysteresis = 0.1f;

    /// <summary>Base cutoffs in pixels at detail 1: the minimum size for L0, L1, L2 and L3. Smaller than the last is L4.</summary>
    public static ReadOnlySpan<float> BaseCutoffs(NodeKind kind) => kind switch
    {
        NodeKind.Document => [240f, 120f, 40f, 10f],
        NodeKind.Idea => [200f, 100f, 32f, 8f],
        NodeKind.NoteInSpace => [320f, 140f, 48f, 10f],
        NodeKind.Label => [160f, 80f, 28f, 8f],
        NodeKind.Image => [260f, 120f, 40f, 10f],
        _ => [200f, 100f, 32f, 8f],
    };

    /// <summary>Cutoff in pixels for the boundary where <paramref name="finer"/> gives way to the next coarser rung, at the given detail.</summary>
    public static float Cutoff(NodeKind kind, NodeDetailLevel finer, float detail)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)finer, (int)NodeDetailLevel.L3);
        return BaseCutoffs(kind)[(int)finer] / ClampDetail(detail);
    }

    /// <summary>Stateless pick, no hysteresis. Smaller on screen never gives more detail.</summary>
    public static NodeDetailLevel Pick(NodeKind kind, float pixelSize, float detail)
    {
        ReadOnlySpan<float> cuts = BaseCutoffs(kind);
        float d = ClampDetail(detail);
        for (int i = 0; i < cuts.Length; i++)
        {
            if (pixelSize >= cuts[i] / d)
            {
                return (NodeDetailLevel)i;
            }
        }

        return NodeDetailLevel.L4;
    }

    /// <summary>Pick with hysteresis against the rung the node had last frame.</summary>
    public static NodeDetailLevel Pick(NodeKind kind, float pixelSize, float detail, NodeDetailLevel previous)
    {
        ReadOnlySpan<float> cuts = BaseCutoffs(kind);
        float d = ClampDetail(detail);
        int level = Math.Clamp((int)previous, 0, cuts.Length);

        // Go finer while the size clears the next finer cutoff by the margin.
        while (level > 0 && pixelSize >= cuts[level - 1] / d * (1f + Hysteresis))
        {
            level--;
        }

        // Go coarser while the size has fallen under this rung's cutoff by the margin.
        while (level < cuts.Length && pixelSize < cuts[level] / d * (1f - Hysteresis))
        {
            level++;
        }

        return (NodeDetailLevel)level;
    }

    /// <summary>What a kind draws on a rung. Every kind is a plain dot on L4; body text shows only on the highest rungs.</summary>
    public static NodeContent ContentFor(NodeKind kind, NodeDetailLevel level) => level switch
    {
        NodeDetailLevel.L4 => NodeContent.Dot,
        NodeDetailLevel.L3 => NodeContent.Marker,
        NodeDetailLevel.L0 => kind switch
        {
            NodeKind.Document => NodeContent.Icon | NodeContent.Title | NodeContent.Text,
            NodeKind.Idea => NodeContent.Icon | NodeContent.Title,
            NodeKind.NoteInSpace => NodeContent.Text,
            NodeKind.Label => NodeContent.Title,
            NodeKind.Image => NodeContent.Icon | NodeContent.Title,
            _ => NodeContent.Icon,
        },
        NodeDetailLevel.L1 => kind switch
        {
            NodeKind.NoteInSpace => NodeContent.Text,
            NodeKind.Label => NodeContent.Title,
            NodeKind.Generic or NodeKind.Group => NodeContent.Icon,
            _ => NodeContent.Icon | NodeContent.Title,
        },
        _ => kind switch
        {
            // Notes and labels have no icon: their text gives way to the marker one rung sooner than icons do.
            NodeKind.NoteInSpace or NodeKind.Label => NodeContent.Marker,
            _ => NodeContent.Icon,
        },
    };

    /// <summary>True when the rung draws any text (title or body) for the kind.</summary>
    public static bool ShowsText(NodeKind kind, NodeDetailLevel level)
        => (ContentFor(kind, level) & (NodeContent.Title | NodeContent.Text)) != 0;

    private static float ClampDetail(float detail)
        => float.IsNaN(detail) ? 1f : Math.Clamp(detail, FrameBudgetController.MinDetail, FrameBudgetController.MaxDetail);
}
