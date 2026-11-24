using Bazaar.Constellation.Communities.Abstractions;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Sections;

/// <summary>Maps community layout output to the renderer's section model (<see cref="SectionDef"/>).</summary>
public static class CommunitySectionAdapter
{
    /// <summary>One root section per community, in the layout's order. Communities are flat, so no parents are set.</summary>
    public static IReadOnlyList<SectionDef> ToSectionDefs(CommunityLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Sections.Select(s => new SectionDef(s.Id, s.Label, s.Members, null, s.Color)).ToArray();
    }

    /// <summary>
    /// Node positions by id, from the packed x,y array. <see cref="SectionSet"/> reads positions by node id, so this is
    /// the lookup a caller hands it.
    /// </summary>
    public static IReadOnlyDictionary<string, (float X, float Y)> PositionsById(CommunityLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var xy = layout.Xy.Span;
        var map = new Dictionary<string, (float, float)>(layout.NodeIds.Count, StringComparer.Ordinal);
        for (var i = 0; i < layout.NodeIds.Count; i++)
        {
            map[layout.NodeIds[i]] = (xy[i * 2], xy[(i * 2) + 1]);
        }

        return map;
    }
}
