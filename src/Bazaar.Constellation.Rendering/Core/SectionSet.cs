namespace Bazaar.Constellation.Rendering.Core;

/// <summary>One caller-supplied section: an id, a label, the member node ids and an optional parent section id.</summary>
public sealed record SectionDef(string Id, string Label, IReadOnlyList<string> Members, string? ParentId = null, uint Color = 0xFFFFFFFFu);

/// <summary>
/// A computed section rectangle in world space with its draw state for the current view. <c>Depth</c> is 0 for roots.
/// <c>Alpha</c> is 0..1 (already scaled by the fade); <c>ShowLabel</c> is false when the rectangle is too small to read a label.
/// </summary>
public readonly record struct SectionRect(
    string Id, string Label, string? ParentId, int Depth, uint Color,
    float MinX, float MinY, float MaxX, float MaxY, float Alpha, bool ShowLabel);

/// <summary>Cull and fade numbers for sections (operator/developer knobs, not a user quality setting).</summary>
public sealed record SectionCullSettings
{
    /// <summary>Padding in world units added around members, and between a child rectangle and its parent's edge.</summary>
    public float Padding { get; init; } = 12f;

    /// <summary>Below this on-screen size (largest side, pixels) the section is not drawn at all.</summary>
    public float MinPixels { get; init; } = 48f;

    /// <summary>At or above this size the section is at full alpha; between <see cref="MinPixels"/> and here it fades in.</summary>
    public float FullAlphaPixels { get; init; } = 160f;

    /// <summary>Below this size the rectangle draws without its label.</summary>
    public float LabelMinPixels { get; init; } = 96f;

    /// <summary>Alpha at full opacity; sections are semi-transparent.</summary>
    public float MaxAlpha { get; init; } = 0.25f;

    /// <summary>A section needs at least this many members on screen to be drawn.</summary>
    public int MinVisibleMembers { get; init; } = 2;

    /// <summary>Per-frame cap; when exceeded the smallest sections are shed first. Zero or less means no cap.</summary>
    public int MaxSections { get; init; } = 64;
}

/// <summary>
/// Pure section model: turns caller-supplied sections plus node positions into semi-transparent labelled rectangles that
/// enclose their members with padding, nest children inside parents, and are culled and faded by projected size.
/// The renderer never derives groups; the caller decides what a section contains. Sections are a backdrop only, so edges
/// that cross a section border are unaffected: they are never clipped or hidden.
/// Output order is deterministic: parents before children (depth ascending), then by id (ordinal). Reuses internal
/// scratch buffers between calls, so the per-section work allocates nothing. Not thread safe.
/// </summary>
public sealed class SectionSet
{
    private const int MaxDepth = 32;

    private readonly SectionDef[] _defs;
    private readonly int[] _order;     // def indices sorted by depth then id
    private readonly int[] _parent;    // parent def index or -1
    private readonly int[] _depth;
    private readonly float[] _minX;
    private readonly float[] _minY;
    private readonly float[] _maxX;
    private readonly float[] _maxY;
    private readonly bool[] _valid;
    private readonly int[] _visible;

    public SectionSet(IReadOnlyList<SectionDef> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        int n = sections.Count;
        _defs = new SectionDef[n];
        for (int i = 0; i < n; i++)
        {
            _defs[i] = sections[i];
        }

        Dictionary<string, int> byId = new(n, StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            byId.TryAdd(_defs[i].Id, i); // duplicate ids: first wins for parent lookup
        }

        _parent = new int[n];
        _depth = new int[n];
        for (int i = 0; i < n; i++)
        {
            _parent[i] = _defs[i].ParentId is { } p && byId.TryGetValue(p, out int pi) && pi != i ? pi : -1;
        }

        for (int i = 0; i < n; i++)
        {
            // A parent chain that loops (or is absurdly deep) makes this section a root.
            int d = 0;
            int cur = i;
            while (_parent[cur] >= 0 && d <= MaxDepth)
            {
                cur = _parent[cur];
                d++;
                if (cur == i)
                {
                    break;
                }
            }

            if (d > MaxDepth || cur == i)
            {
                _parent[i] = -1;
                d = 0;
            }

            _depth[i] = d;
        }

        _order = new int[n];
        for (int i = 0; i < n; i++)
        {
            _order[i] = i;
        }

        Array.Sort(_order, (a, b) =>
        {
            int c = _depth[a].CompareTo(_depth[b]);
            return c != 0 ? c : string.CompareOrdinal(_defs[a].Id, _defs[b].Id);
        });

        _minX = new float[n];
        _minY = new float[n];
        _maxX = new float[n];
        _maxY = new float[n];
        _valid = new bool[n];
        _visible = new int[n];
    }

    public int Count => _defs.Length;

    /// <summary>
    /// Computes the sections to draw for this view into <paramref name="output"/> (cleared first). Members with no
    /// position are ignored; a section with no positioned members and no drawable child is skipped. Parent rectangles
    /// grow to contain their children plus padding. Sections wholly outside the view, smaller than the pixel threshold,
    /// or with too few members on screen are skipped; the rest are faded by projected size.
    /// </summary>
    public void Compute(
        IReadOnlyDictionary<string, (float X, float Y)> positions,
        Camera2D camera, int widthPixels, int heightPixels,
        SectionCullSettings settings, List<SectionRect> output)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();

        float zoom = camera.Zoom;
        float halfW = widthPixels * 0.5f / zoom;
        float halfH = heightPixels * 0.5f / zoom;
        float viewMinX = camera.CenterX - halfW;
        float viewMaxX = camera.CenterX + halfW;
        float viewMinY = camera.CenterY - halfH;
        float viewMaxY = camera.CenterY + halfH;
        float pad = settings.Padding;

        for (int i = 0; i < _defs.Length; i++)
        {
            _valid[i] = false;
            _visible[i] = 0;
            _minX[i] = float.PositiveInfinity;
            _minY[i] = float.PositiveInfinity;
            _maxX[i] = float.NegativeInfinity;
            _maxY[i] = float.NegativeInfinity;
            IReadOnlyList<string> members = _defs[i].Members;
            for (int m = 0; m < members.Count; m++)
            {
                if (!positions.TryGetValue(members[m], out (float X, float Y) p))
                {
                    continue;
                }

                _valid[i] = true;
                _minX[i] = MathF.Min(_minX[i], p.X - pad);
                _minY[i] = MathF.Min(_minY[i], p.Y - pad);
                _maxX[i] = MathF.Max(_maxX[i], p.X + pad);
                _maxY[i] = MathF.Max(_maxY[i], p.Y + pad);
                if (p.X >= viewMinX && p.X <= viewMaxX && p.Y >= viewMinY && p.Y <= viewMaxY)
                {
                    _visible[i]++;
                }
            }
        }

        // Deepest first, so a child is final before its parent absorbs it.
        for (int k = _order.Length - 1; k >= 0; k--)
        {
            int i = _order[k];
            int p = _parent[i];
            if (p < 0 || !_valid[i])
            {
                continue;
            }

            _valid[p] = true;
            _minX[p] = MathF.Min(_minX[p], _minX[i] - pad);
            _minY[p] = MathF.Min(_minY[p], _minY[i] - pad);
            _maxX[p] = MathF.Max(_maxX[p], _maxX[i] + pad);
            _maxY[p] = MathF.Max(_maxY[p], _maxY[i] + pad);
            _visible[p] += _visible[i];
        }

        float span = MathF.Max(settings.FullAlphaPixels - settings.MinPixels, 1e-3f);
        for (int k = 0; k < _order.Length; k++)
        {
            int i = _order[k];
            if (!_valid[i]
                || _maxX[i] < viewMinX || _minX[i] > viewMaxX || _maxY[i] < viewMinY || _minY[i] > viewMaxY
                || _visible[i] < settings.MinVisibleMembers)
            {
                continue;
            }

            float px = MathF.Max(_maxX[i] - _minX[i], _maxY[i] - _minY[i]) * zoom;
            if (px < settings.MinPixels)
            {
                continue;
            }

            float fade = Math.Clamp((px - settings.MinPixels) / span, 0f, 1f);
            SectionDef d = _defs[i];
            output.Add(new SectionRect(
                d.Id, d.Label, _parent[i] >= 0 ? _defs[_parent[i]].Id : null, _depth[i], d.Color,
                _minX[i], _minY[i], _maxX[i], _maxY[i], fade * settings.MaxAlpha, px >= settings.LabelMinPixels));
        }

        if (settings.MaxSections > 0 && output.Count > settings.MaxSections)
        {
            // Shed the smallest first; ties break on id so the result is deterministic. Output stays in draw order.
            int excess = output.Count - settings.MaxSections;
            List<SectionRect> sorted = new(output);
            sorted.Sort(static (a, b) =>
            {
                int c = Area(a).CompareTo(Area(b));
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });
            HashSet<string> shed = new(StringComparer.Ordinal);
            for (int s = 0; s < excess; s++)
            {
                shed.Add(sorted[s].Id);
            }

            output.RemoveAll(r => shed.Contains(r.Id));
        }
    }

    private static float Area(in SectionRect r) => (r.MaxX - r.MinX) * (r.MaxY - r.MinY);
}
