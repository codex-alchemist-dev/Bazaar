using System.Runtime.InteropServices;

namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Packs the per-instance icon data that sits beside <see cref="NodeInstanceBuffer"/>: four floats per node, the atlas
/// rectangle (u0, v0, u1, v1) of the glyph to draw. A rectangle of all zeros means "no icon", and the shader draws the
/// plain dot or ring for it. The rung comes from <see cref="NodeLodPolicy"/> (with hysteresis against the rung the node
/// had on the last fill), and the glyph from <see cref="IconAtlas.GlyphFor"/>. Every rung except the farthest draws its
/// glyph; L4 stays a dot so far-away nodes merge into the density field. Pure CPU, no device objects.
/// </summary>
public sealed class NodeIconBuffer
{
    /// <summary>Floats per instance: u0, v0, u1, v1.</summary>
    public const int FloatsPerInstance = 4;

    private float[] _floats = [];
    private NodeDetailLevel[] _levels = [];
    private int _instanceCount;

    /// <summary>Number of nodes packed by the last <see cref="Fill"/>.</summary>
    public int InstanceCount => _instanceCount;

    /// <summary>The packed rectangles: <see cref="InstanceCount"/> times <see cref="FloatsPerInstance"/> floats.</summary>
    public ReadOnlySpan<float> Floats => _floats.AsSpan(0, _instanceCount * FloatsPerInstance);

    /// <summary>The packed rectangles as raw bytes, for a vertex buffer upload.</summary>
    public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(Floats);

    /// <summary>The rung chosen for each node by the last <see cref="Fill"/>.</summary>
    public ReadOnlySpan<NodeDetailLevel> Levels => _levels.AsSpan(0, _instanceCount);

    /// <summary>
    /// Picks a rung per node from its kind and projected size in pixels, then writes its glyph rectangle. When
    /// <paramref name="enabled"/> is false every rectangle is zero (all dots). Nothing changes when the arguments are rejected.
    /// </summary>
    /// <param name="detail">Live detail scale from <see cref="FrameBudgetController.Detail"/>.</param>
    public void Fill(ReadOnlySpan<NodeKind> kinds, ReadOnlySpan<float> pixelSizes, float detail, IconAtlas atlas, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        if (kinds.Length != pixelSizes.Length)
        {
            throw new ArgumentException("Kinds and pixel sizes must have the same length.", nameof(pixelSizes));
        }

        int count = kinds.Length;
        if (_floats.Length < count * FloatsPerInstance)
        {
            _floats = new float[count * FloatsPerInstance];
        }

        if (_levels.Length < count)
        {
            // New entries start at L0, so the first pick settles on the right rung by size alone.
            Array.Resize(ref _levels, count);
        }

        for (int node = 0; node < count; node++)
        {
            int target = node * FloatsPerInstance;
            NodeDetailLevel level = NodeLodPolicy.Pick(kinds[node], pixelSizes[node], detail, _levels[node]);
            _levels[node] = level;

            if (!enabled || level == NodeDetailLevel.L4 || !atlas.TryGetUv(kinds[node], level, out AtlasCell cell))
            {
                _floats[target] = 0f;
                _floats[target + 1] = 0f;
                _floats[target + 2] = 0f;
                _floats[target + 3] = 0f;
                continue;
            }

            _floats[target] = cell.U0;
            _floats[target + 1] = cell.V0;
            _floats[target + 2] = cell.U1;
            _floats[target + 3] = cell.V1;
        }

        _instanceCount = count;
    }

    /// <summary>Forgets the packed rectangles and the remembered rungs.</summary>
    public void Clear()
    {
        _instanceCount = 0;
        Array.Clear(_levels);
    }
}
