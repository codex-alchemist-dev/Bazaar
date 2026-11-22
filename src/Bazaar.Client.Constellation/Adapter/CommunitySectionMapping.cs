using Bazaar.Constellation.Communities.Abstractions;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Client.Constellation.Adapter;

/// <summary>
/// Maps the community layout's sections onto the renderer's section model. The community module has no renderer
/// dependency by design, so the mapping lives here, on the Bazaar side.
/// </summary>
public static class CommunitySectionMapping
{
    /// <summary>One <see cref="SectionDef"/> per community section: same id, label, members and colour, no parent (communities do not nest).</summary>
    public static IReadOnlyList<SectionDef> ToSectionDefs(this CommunityLayoutResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var defs = new List<SectionDef>(result.Sections.Count);
        foreach (var s in result.Sections)
        {
            defs.Add(new SectionDef(s.Id, s.Label, s.Members, null, s.Color));
        }

        return defs;
    }
}
