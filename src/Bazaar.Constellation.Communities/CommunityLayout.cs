using Bazaar.Constellation.Communities.Abstractions;
using Bazaar.Constellation.Communities.Detection;

namespace Bazaar.Constellation.Communities;

/// <summary>
/// Louvain community layout. Detects communities, packs one disc per community on a golden-angle spiral without
/// overlap, and places each community's nodes on a golden-angle spiral inside its disc with the best-connected nodes
/// in the middle. There is no force simulation, so the cost is Louvain plus a linear pass, and the same input always
/// gives the same output. Each community becomes a labelled section with a golden-ratio hue.
/// </summary>
public sealed class CommunityLayout : ICommunityLayout
{
    private const double _goldenAngle = 2.399963229728653; // pi * (3 - sqrt 5)
    private const double _goldenRatioConjugate = 0.6180339887498949;

    private readonly TaskScheduler _scheduler;

    /// <param name="scheduler">Where the work runs. Defaults to the thread pool, never the caller's context.</param>
    public CommunityLayout(TaskScheduler? scheduler = null) => _scheduler = scheduler ?? TaskScheduler.Default;

    public Task<CommunityLayoutResult> ComputeAsync(
        IReadOnlyList<CommunityNode> nodes,
        IReadOnlyList<CommunityEdge> edges,
        CommunityLayoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        var opts = options ?? new CommunityLayoutOptions();
        return Task.Factory.StartNew(
            () => Compute(nodes, edges, opts, cancellationToken),
            cancellationToken,
            TaskCreationOptions.DenyChildAttach,
            _scheduler);
    }

    private static CommunityLayoutResult Compute(
        IReadOnlyList<CommunityNode> nodes, IReadOnlyList<CommunityEdge> edges, CommunityLayoutOptions options, CancellationToken ct)
    {
        var labelById = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.IsNullOrEmpty(node.Id))
            {
                throw new ArgumentException("Node ids must be non-empty.", nameof(nodes));
            }

            labelById.TryAdd(node.Id, node.Label);
        }

        var ids = labelById.Keys.Order(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return new CommunityLayoutResult([], ReadOnlyMemory<float>.Empty, [], 0);
        }

        ct.ThrowIfCancellationRequested();
        var louvain = LouvainClustering.Detect(ids, edges, options.Resolution);

        var degree = new Dictionary<string, double>(ids.Length, StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (edge.From == edge.To || edge.Weight <= 0)
            {
                continue;
            }

            degree[edge.From] = degree.GetValueOrDefault(edge.From) + edge.Weight;
            degree[edge.To] = degree.GetValueOrDefault(edge.To) + edge.Weight;
        }

        ct.ThrowIfCancellationRequested();
        var groups = BuildGroups(ids, louvain.CommunityByNode, degree, options.MaxSections);

        var spacing = Math.Max(options.NodeSpacing, 1e-3f);
        var radii = groups.Select(g => spacing * (float)Math.Sqrt(g.Members.Count + 0.5)).ToArray();
        var centres = PackDiscs(radii, Math.Max(options.CommunityGap, 0f), ct);

        var index = new Dictionary<string, int>(ids.Length, StringComparer.Ordinal);
        for (var i = 0; i < ids.Length; i++)
        {
            index[ids[i]] = i;
        }

        var xy = new float[ids.Length * 2];
        var sections = new List<CommunitySection>(groups.Count);
        for (var g = 0; g < groups.Count; g++)
        {
            ct.ThrowIfCancellationRequested();
            var members = groups[g].Members;
            for (var k = 0; k < members.Count; k++)
            {
                var r = spacing * Math.Sqrt(k + 0.5);
                var a = k * _goldenAngle;
                var slot = index[members[k]] * 2;
                xy[slot] = centres[g].X + (float)(r * Math.Cos(a));
                xy[slot + 1] = centres[g].Y + (float)(r * Math.Sin(a));
            }

            var top = members[0];
            var name = labelById[top] ?? top;
            var label = groups[g].IsOther ? $"Other ({members.Count})" : $"{name} ({members.Count})";
            sections.Add(new CommunitySection(
                groups[g].IsOther ? "community:other" : $"community:{g}",
                label,
                members,
                GoldenColor(g),
                centres[g].X,
                centres[g].Y,
                radii[g]));
        }

        return new CommunityLayoutResult(ids, xy, sections, louvain.Modularity);
    }

    private sealed record Group(List<string> Members, bool IsOther);

    /// <summary>
    /// One group per community, largest first (ties by smallest member id), members by degree descending then id.
    /// When there are more than <paramref name="maxSections"/> communities the tail collapses into one "Other" group.
    /// </summary>
    private static List<Group> BuildGroups(
        string[] ids, IReadOnlyDictionary<string, int> communityByNode, Dictionary<string, double> degree, int maxSections)
    {
        var byCommunity = new Dictionary<int, List<string>>();
        foreach (var id in ids)
        {
            var c = communityByNode[id];
            if (!byCommunity.TryGetValue(c, out var list))
            {
                byCommunity[c] = list = [];
            }

            list.Add(id);
        }

        // ids are already ordinal sorted, so list[0] is each community's smallest id.
        var ordered = byCommunity.Values
            .OrderByDescending(l => l.Count)
            .ThenBy(l => l[0], StringComparer.Ordinal)
            .ToList();

        var groups = new List<Group>();
        var named = maxSections > 0 && ordered.Count > maxSections ? Math.Max(maxSections - 1, 0) : ordered.Count;
        for (var i = 0; i < named; i++)
        {
            groups.Add(new Group(SortByDegree(ordered[i], degree), false));
        }

        if (named < ordered.Count)
        {
            var tail = ordered.Skip(named).SelectMany(l => l).ToList();
            groups.Add(new Group(SortByDegree(tail, degree), true));
        }

        return groups;
    }

    private static List<string> SortByDegree(List<string> members, Dictionary<string, double> degree) =>
        members
            .OrderByDescending(id => degree.GetValueOrDefault(id))
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Places discs largest first: each takes the first point on a golden-angle spiral around the origin where it clears
    /// every disc already placed by <paramref name="gap"/>. Deterministic, and quadratic only in the (capped) group count.
    /// </summary>
    private static (float X, float Y)[] PackDiscs(float[] radii, float gap, CancellationToken ct)
    {
        var centres = new (float X, float Y)[radii.Length];
        for (var i = 0; i < radii.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i == 0)
            {
                continue; // the largest group sits at the origin
            }

            var step = Math.Max(radii[i], 1f) * 0.25;
            for (var t = 1; ; t++)
            {
                var dist = step * Math.Sqrt(t);
                var angle = t * _goldenAngle;
                var x = (float)(dist * Math.Cos(angle));
                var y = (float)(dist * Math.Sin(angle));
                if (Clear(centres, radii, i, x, y, gap))
                {
                    centres[i] = (x, y);
                    break;
                }
            }
        }

        return centres;
    }

    private static bool Clear((float X, float Y)[] centres, float[] radii, int count, float x, float y, float gap)
    {
        for (var j = 0; j < count; j++)
        {
            var dx = x - centres[j].X;
            var dy = y - centres[j].Y;
            var min = radii[j] + radii[count] + gap;
            if ((dx * dx) + (dy * dy) < min * min)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Golden-ratio hue per section index, fixed saturation and value, as opaque ARGB.</summary>
    internal static uint GoldenColor(int index)
    {
        var h = ((index * _goldenRatioConjugate) + 0.15) % 1.0 * 6.0;
        const double s = 0.65;
        const double v = 0.95;
        var sector = (int)h;
        var f = h - sector;
        var p = v * (1 - s);
        var q = v * (1 - (s * f));
        var u = v * (1 - (s * (1 - f)));
        var (r, g, b) = sector switch
        {
            0 => (v, u, p),
            1 => (q, v, p),
            2 => (p, v, u),
            3 => (p, q, v),
            4 => (u, p, v),
            _ => (v, p, q),
        };
        return 0xFF000000u | ((uint)Math.Round(r * 255) << 16) | ((uint)Math.Round(g * 255) << 8) | (uint)Math.Round(b * 255);
    }
}
