using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Client.Constellation.Adapter;

/// <summary>
/// The Bazaar side of the renderer seam: turns Constellation graph data into the renderer's inputs (sections, node kinds,
/// connections). Pure and deterministic: the same nodes give the same output in the same order, whatever order they arrive in.
/// A section is always an explicit group, never a guess from layout. Precedence for a node's single section: its front-matter
/// <c>group</c>, then a group note that lists it, then its folder. Documents connect only through links in their content;
/// nodes without content (and sticky notes) connect through hidden metadata. Ids that are not in the node set are ignored.
/// </summary>
public sealed class ConstellationSectionAdapter
{
    private const string _adHocPrefix = "group:";
    private const string _folderPrefix = "folder:";

    private readonly ConstellationAdapterSettings _settings;

    public ConstellationSectionAdapter(ConstellationAdapterSettings? settings = null) =>
        _settings = settings ?? new ConstellationAdapterSettings();

    /// <summary>Maps a Bazaar type string to the renderer kind; unknown or empty types are <see cref="ConstellationNodeKind.Generic"/>.</summary>
    public ConstellationNodeKind MapKind(string? type) =>
        type is not null && _settings.KindMap.TryGetValue(type, out ConstellationNodeKind kind) ? kind : ConstellationNodeKind.Generic;

    /// <summary>Kind for every node id, in ordinal id order.</summary>
    public IReadOnlyDictionary<string, ConstellationNodeKind> MapKinds(IEnumerable<ConstellationSourceNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        SortedDictionary<string, ConstellationNodeKind> map = new(StringComparer.Ordinal);
        foreach (ConstellationSourceNode n in nodes)
        {
            map[n.Id] = MapKind(n.Type);
        }

        return map;
    }

    /// <summary>The connections the renderer draws, sorted by source then target, without duplicates, self-links or dangling ends.</summary>
    public IReadOnlyList<ConstellationConnection> DeriveConnections(IEnumerable<ConstellationSourceNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        List<ConstellationSourceNode> list = Distinct(nodes);
        HashSet<string> ids = new(list.Select(n => n.Id), StringComparer.Ordinal);
        SortedSet<(string, string)> edges = [];
        foreach (ConstellationSourceNode n in list)
        {
            IReadOnlyList<string>? source = UsesMetadataLinks(n) ? n.MetadataLinks : n.ContentLinks;
            if (source is null)
            {
                continue;
            }

            foreach (string target in source)
            {
                if (target != n.Id && ids.Contains(target))
                {
                    edges.Add((n.Id, target));
                }
            }
        }

        return [.. edges.Select(e => new ConstellationConnection(e.Item1, e.Item2))];
    }

    /// <summary>Sections from explicit groups: ordinal by id, empty sections dropped, parents named by id.</summary>
    public IReadOnlyList<SectionDef> BuildSections(IEnumerable<ConstellationSourceNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        List<ConstellationSourceNode> list = Distinct(nodes);
        HashSet<string> ids = new(list.Select(n => n.Id), StringComparer.Ordinal);
        Dictionary<string, ConstellationSourceNode> groupNotes = new(StringComparer.Ordinal);
        foreach (ConstellationSourceNode n in list)
        {
            if (_settings.GroupNoteTypes.Contains(n.Type))
            {
                groupNotes[n.Id] = n;
            }
        }

        Dictionary<string, string> byTitle = new(StringComparer.OrdinalIgnoreCase);
        foreach (ConstellationSourceNode g in groupNotes.Values)
        {
            if (!string.IsNullOrWhiteSpace(g.Title))
            {
                byTitle.TryAdd(g.Title, g.Id); // list is id-sorted, so the lowest id wins a title clash
            }
        }

        Dictionary<string, SectionBuilder> sections = new(StringComparer.Ordinal);
        foreach (ConstellationSourceNode g in groupNotes.Values)
        {
            sections[g.Id] = new SectionBuilder(g.Id, string.IsNullOrWhiteSpace(g.Title) ? g.Id : g.Title);
        }

        // A group note's listed members: each member joins the first (lowest id) note that lists it.
        Dictionary<string, string> listedBy = new(StringComparer.Ordinal);
        foreach (ConstellationSourceNode g in groupNotes.Values)
        {
            foreach (string m in g.ContentLinks ?? [])
            {
                if (m != g.Id && ids.Contains(m))
                {
                    listedBy.TryAdd(m, g.Id);
                }
            }
        }

        // Assign each node to exactly one section.
        Dictionary<string, string> sectionOf = new(StringComparer.Ordinal);
        foreach (ConstellationSourceNode n in list)
        {
            string? sid = !string.IsNullOrWhiteSpace(n.Group)
                ? ResolveGroup(n.Group, groupNotes, byTitle, sections)
                : null;
            if (sid is null && listedBy.TryGetValue(n.Id, out string? noteId))
            {
                sid = noteId;
            }

            if (sid is null && _settings.FolderSections && !string.IsNullOrEmpty(n.FolderPath))
            {
                sid = ResolveFolder(n.FolderPath, sections);
            }

            if (sid is not null && sid != n.Id)
            {
                sectionOf[n.Id] = sid;
            }
        }

        foreach (KeyValuePair<string, string> kv in sectionOf)
        {
            if (groupNotes.ContainsKey(kv.Key))
            {
                sections[kv.Key].ParentId = kv.Value; // a group note placed in another group nests under it
            }
            else
            {
                sections[kv.Value].Members.Add(kv.Key);
            }
        }

        BreakCycles(sections);
        return Emit(sections);
    }

    private List<SectionDef> Emit(Dictionary<string, SectionBuilder> sections)
    {
        // Drop sections that hold nothing, directly or through descendants; folders also need the minimum member count.
        Dictionary<string, int> total = new(StringComparer.Ordinal);
        int Count(SectionBuilder s, int guard)
        {
            if (total.TryGetValue(s.Id, out int cached))
            {
                return cached;
            }

            int sum = s.Members.Count;
            if (guard < 64)
            {
                foreach (SectionBuilder child in sections.Values.Where(c => c.ParentId == s.Id))
                {
                    sum += Count(child, guard + 1);
                }
            }

            total[s.Id] = sum;
            return sum;
        }

        HashSet<string> keep = new(StringComparer.Ordinal);
        foreach (SectionBuilder s in sections.Values)
        {
            int n = Count(s, 0);
            bool folder = s.Id.StartsWith(_folderPrefix, StringComparison.Ordinal);
            if (n > 0 && (!folder || n >= _settings.MinFolderMembers))
            {
                keep.Add(s.Id);
            }
        }

        List<SectionDef> result = [];
        foreach (SectionBuilder s in sections.Values.Where(s => keep.Contains(s.Id)).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            string? parent = s.ParentId;
            while (parent is not null && !keep.Contains(parent))
            {
                parent = sections[parent].ParentId; // skip over a dropped ancestor
            }

            string[] members = [.. s.Members.Order(StringComparer.Ordinal)];
            result.Add(new SectionDef(s.Id, s.Label, members, parent, _settings.SectionColor));
        }

        return result;
    }

    private string ResolveGroup(string group, Dictionary<string, ConstellationSourceNode> notes, Dictionary<string, string> byTitle, Dictionary<string, SectionBuilder> sections)
    {
        string value = group.Trim();
        if (notes.ContainsKey(value))
        {
            return value;
        }

        if (byTitle.TryGetValue(value, out string? id))
        {
            return id;
        }

        string[] parts = value.Split(_settings.GroupPathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return value;
        }

        return ResolvePath(parts, parts.Length, _adHocPrefix, _settings.GroupPathSeparator.ToString(), sections);
    }

    private string? ResolveFolder(string folderPath, Dictionary<string, SectionBuilder> sections)
    {
        string[] parts = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        return ResolvePath(parts, Math.Min(parts.Length, Math.Max(1, _settings.MaxFolderDepth)), _folderPrefix, "/", sections);
    }

    private static string ResolvePath(string[] parts, int depth, string prefix, string separator, Dictionary<string, SectionBuilder> sections)
    {
        string? parent = null;
        string path = "";
        string sid = "";
        for (int i = 0; i < depth; i++)
        {
            path = path.Length == 0 ? parts[i] : path + separator + parts[i];
            sid = prefix + path;
            if (!sections.TryGetValue(sid, out SectionBuilder? b))
            {
                b = new SectionBuilder(sid, parts[i]) { ParentId = parent };
                sections[sid] = b;
            }

            parent = sid;
        }

        return sid;
    }

    private static void BreakCycles(Dictionary<string, SectionBuilder> sections)
    {
        foreach (SectionBuilder s in sections.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            string? cur = s.ParentId;
            for (int steps = 0; cur is not null && steps <= sections.Count; steps++)
            {
                if (cur == s.Id)
                {
                    s.ParentId = null;
                    break;
                }

                cur = sections.TryGetValue(cur, out SectionBuilder? p) ? p.ParentId : null;
            }
        }
    }

    private bool UsesMetadataLinks(ConstellationSourceNode n) => !n.HasContent || _settings.MetadataLinkTypes.Contains(n.Type);

    // Id-sorted, first wins on a duplicate id, blank ids dropped.
    private static List<ConstellationSourceNode> Distinct(IEnumerable<ConstellationSourceNode> nodes)
    {
        SortedDictionary<string, ConstellationSourceNode> map = new(StringComparer.Ordinal);
        foreach (ConstellationSourceNode n in nodes)
        {
            if (!string.IsNullOrEmpty(n.Id))
            {
                map.TryAdd(n.Id, n);
            }
        }

        return [.. map.Values];
    }

    private sealed class SectionBuilder(string id, string label)
    {
        public string Id { get; } = id;

        public string Label { get; } = label;

        public string? ParentId { get; set; }

        public List<string> Members { get; } = [];
    }
}
