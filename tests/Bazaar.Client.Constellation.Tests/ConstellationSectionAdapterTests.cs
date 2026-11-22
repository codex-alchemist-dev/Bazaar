using Bazaar.Client.Constellation.Adapter;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Client.Constellation.Tests;

public class ConstellationSectionAdapterTests
{
    private static readonly ConstellationSectionAdapter _adapter = new();

    private static ConstellationSourceNode Doc(string id, string? group = null, string? folder = null, params string[] links) =>
        new(id, "plan", Group: group, FolderPath: folder, ContentLinks: links);

    [Fact]
    public void GroupFromFrontMatter()
    {
        IReadOnlyList<SectionDef> s = _adapter.BuildSections([Doc("a", "Auth"), Doc("b", "Auth"), Doc("c")]);
        SectionDef sec = Assert.Single(s);
        Assert.Equal("Auth", sec.Label);
        Assert.Equal(["a", "b"], sec.Members);
        Assert.Null(sec.ParentId);
    }

    [Fact]
    public void FolderMembershipNestsAndNeedsMinimum()
    {
        IReadOnlyList<SectionDef> s = _adapter.BuildSections(
            [Doc("a", folder: "p"), Doc("b", folder: "p/x"), Doc("c", folder: "p/x"), Doc("d", folder: "lone/y")]);
        Assert.Equal(["folder:p", "folder:p/x"], s.Select(x => x.Id));
        Assert.Equal("folder:p", s[1].ParentId);
        Assert.Equal(["a"], s[0].Members);
    }

    [Fact]
    public void FrontMatterBeatsFolder()
    {
        IReadOnlyList<SectionDef> s = _adapter.BuildSections([Doc("a", "G", "f"), Doc("b", "G", "f"), Doc("c", folder: "f")]);
        Assert.Contains(s, x => x.Label == "G" && x.Members.SequenceEqual(["a", "b"]));
        Assert.DoesNotContain(s, x => x.Id.StartsWith("folder:", StringComparison.Ordinal)); // only one node left in the folder
    }

    [Fact]
    public void GroupNoteListsMembersAndIgnoresMissing()
    {
        ConstellationSourceNode note = new("g1", "group", "Release", ContentLinks: ["a", "b", "ghost"]);
        IReadOnlyList<SectionDef> s = _adapter.BuildSections([note, Doc("a"), Doc("b"), Doc("c")]);
        SectionDef sec = Assert.Single(s);
        Assert.Equal("g1", sec.Id);
        Assert.Equal("Release", sec.Label);
        Assert.Equal(["a", "b"], sec.Members);
    }

    [Fact]
    public void NestedGroupsFromNotesAndPaths()
    {
        ConstellationSourceNode outer = new("g-out", "group", "Outer", ContentLinks: ["a"]);
        ConstellationSourceNode inner = new("g-in", "group", "Inner", Group: "Outer", ContentLinks: ["b"]);
        IReadOnlyList<SectionDef> s = _adapter.BuildSections([outer, inner, Doc("a"), Doc("b"), Doc("c", "Top/Sub"), Doc("d", "Top/Sub")]);
        Assert.Equal("g-out", s.Single(x => x.Id == "g-in").ParentId);
        SectionDef sub = s.Single(x => x.Id == "group:Top/Sub");
        Assert.Equal("group:Top", sub.ParentId); // "Top" has no direct members but is kept as the frame of its child
        Assert.Equal("Sub", sub.Label);
        Assert.Empty(s.Single(x => x.Id == "group:Top").Members);
    }

    [Fact]
    public void ParentCycleIsBroken()
    {
        ConstellationSourceNode a = new("a", "group", "A", Group: "B", ContentLinks: ["x"]);
        ConstellationSourceNode b = new("b", "group", "B", Group: "A", ContentLinks: ["y"]);
        IReadOnlyList<SectionDef> s = _adapter.BuildSections([a, b, Doc("x"), Doc("y")]);
        Assert.Equal(2, s.Count);
        Assert.Contains(s, x => x.ParentId is null);
    }

    [Fact]
    public void KindMappingFallsBackToGeneric()
    {
        Assert.Equal(ConstellationNodeKind.Document, _adapter.MapKind("PLAN"));
        Assert.Equal(ConstellationNodeKind.Idea, _adapter.MapKind("idea"));
        Assert.Equal(ConstellationNodeKind.NoteInSpace, _adapter.MapKind("sticky"));
        Assert.Equal(ConstellationNodeKind.Generic, _adapter.MapKind("mystery"));
        Assert.Equal(ConstellationNodeKind.Generic, _adapter.MapKind(null));
    }

    [Fact]
    public void ContentVersusMetadataConnections()
    {
        ConstellationSourceNode doc = new("doc", "plan", ContentLinks: ["open"], MetadataLinks: ["sticky"]);
        ConstellationSourceNode open = new("open", "note", HasContent: false, MetadataLinks: ["doc"], ContentLinks: ["sticky"]);
        ConstellationSourceNode sticky = new("sticky", "sticky", MetadataLinks: ["doc", "doc", "sticky", "ghost"]);
        IReadOnlyList<ConstellationConnection> c = _adapter.DeriveConnections([doc, open, sticky]);
        Assert.Equal(
            [new ConstellationConnection("doc", "open"), new ConstellationConnection("open", "doc"), new ConstellationConnection("sticky", "doc")],
            c);
    }

    [Fact]
    public void OutputIsDeterministicRegardlessOfInputOrder()
    {
        ConstellationSourceNode[] nodes =
        [
            Doc("c", "G", "f", "a"), Doc("a", "G", "f", "b"), Doc("b", folder: "f/g"), new("n", "group", "N", ContentLinks: ["b", "c"]),
        ];
        ConstellationSourceNode[] reversed = [.. nodes.Reverse()];
        Assert.Equal(_adapter.BuildSections(nodes), _adapter.BuildSections(reversed), SectionComparer.Instance);
        Assert.Equal(_adapter.DeriveConnections(nodes), _adapter.DeriveConnections(reversed));
    }

    private sealed class SectionComparer : IEqualityComparer<SectionDef>
    {
        public static readonly SectionComparer Instance = new();

        public bool Equals(SectionDef? x, SectionDef? y) =>
            x is not null && y is not null && x.Id == y.Id && x.Label == y.Label && x.ParentId == y.ParentId && x.Members.SequenceEqual(y.Members);

        public int GetHashCode(SectionDef obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
