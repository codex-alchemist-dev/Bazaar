using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>The pure memory graph projection: links, provenance, ordering and validation. No database needed.</summary>
public sealed class MemoryGraphProjectorTests
{
    private const string Path = "agents/alpha/memory/lessons/retry-backoff.md";
    private const string Agent = "alpha";
    private const string SelfId = "01SELF";
    private const string OtherId = "01OTHER";
    private const string ThirdId = "01THIRD";

    private static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        ["Other note"] = OtherId,
        ["Third note"] = ThirdId,
        ["Self note"] = SelfId,
    };

    private static MemoryNote Note(
        IReadOnlyList<string>? derivedFrom = null,
        IReadOnlyList<string>? supersedes = null,
        MemoryOrigin? origin = null,
        long version = 3) =>
        new(
            Id: SelfId,
            Type: MemoryNoteTypes.TypeFor(MemoryKind.Procedural),
            Title: "Retry backoff",
            Project: null,
            Kind: MemoryKind.Procedural,
            Status: MemoryNoteStatus.Active,
            Version: version,
            Trust: MemoryTier.AgentVerified,
            SourceTrust: ContentTrust.Agent,
            Tainted: false,
            Origin: origin,
            DerivedFrom: derivedFrom ?? [],
            Subjects: [],
            Deprecated: null,
            Supersedes: supersedes ?? []);

    private static MemoryGraphRevision Project(string body, MemoryNote? note = null) =>
        MemoryGraphProjector.Project(note ?? Note(), body, Path, Agent, Titles);

    [Fact]
    public void Revision_fields_come_from_the_note_and_arguments()
    {
        var revision = Project("", Note(version: 7));

        Assert.Equal(SelfId, revision.NoteId);
        Assert.Equal(7, revision.Version);
        Assert.Equal(Agent, revision.ScopeAgentId);
        Assert.Equal(Path, revision.VaultPath);
        Assert.Equal("Retry backoff", revision.Title);
        Assert.Equal("Procedural", revision.Kind);
        Assert.Equal("Active", revision.Status);
        Assert.Equal("AgentVerified", revision.Trust);
    }

    [Fact]
    public void Empty_body_gives_only_the_provenance_edge()
    {
        var revision = Project("");

        var edge = Assert.Single(revision.Edges);
        Assert.Equal(new MemoryGraphEdge("agent:" + Agent, MemoryEdgeKind.AuthoredBy, 0.0), edge);
    }

    [Fact]
    public void Wiki_link_resolves_by_title()
    {
        var revision = Project("See [[Other note]] for details.");

        Assert.Contains(new MemoryGraphEdge(OtherId, MemoryEdgeKind.Link, 1.0), revision.Edges);
    }

    [Fact]
    public void Wiki_link_resolves_by_note_id()
    {
        var revision = Project($"Builds on [[{ThirdId}]].");

        Assert.Contains(new MemoryGraphEdge(ThirdId, MemoryEdgeKind.Link, 1.0), revision.Edges);
    }

    [Fact]
    public void Wiki_link_title_match_is_case_sensitive()
    {
        var revision = Project("See [[other note]].");

        Assert.DoesNotContain(revision.Edges, e => e.Kind == MemoryEdgeKind.Link);
    }

    [Fact]
    public void Dangling_link_produces_no_edge()
    {
        var revision = Project("See [[Nowhere]] and [[01MISSING]].");

        Assert.DoesNotContain(revision.Edges, e => e.Kind == MemoryEdgeKind.Link);
    }

    [Fact]
    public void Alias_after_pipe_uses_the_part_before_the_pipe()
    {
        var revision = Project("Read [[Other note|the other one]].");

        var link = Assert.Single(revision.Edges, e => e.Kind == MemoryEdgeKind.Link);
        Assert.Equal(new MemoryGraphEdge(OtherId, MemoryEdgeKind.Link, 1.0), link);
    }

    [Fact]
    public void Whitespace_inside_brackets_is_trimmed()
    {
        var revision = Project("See [[  Third note  ]].");

        Assert.Contains(new MemoryGraphEdge(ThirdId, MemoryEdgeKind.Link, 1.0), revision.Edges);
    }

    [Fact]
    public void Self_link_by_title_or_id_produces_no_edge()
    {
        var revision = Project($"[[Self note]] and [[{SelfId}]].");

        Assert.DoesNotContain(revision.Edges, e => e.Kind == MemoryEdgeKind.Link);
    }

    [Fact]
    public void Duplicate_links_count_once()
    {
        var revision = Project("[[Other note]] then [[Other note]] and [[01OTHER]].");

        Assert.Single(revision.Edges, e => e.Kind == MemoryEdgeKind.Link);
    }

    [Fact]
    public void Derived_from_and_supersedes_take_their_weights()
    {
        var revision = Project("", Note(derivedFrom: [OtherId], supersedes: [ThirdId]));

        Assert.Contains(new MemoryGraphEdge(OtherId, MemoryEdgeKind.DerivedFrom, 0.8), revision.Edges);
        Assert.Contains(new MemoryGraphEdge(ThirdId, MemoryEdgeKind.Supersedes, 0.5), revision.Edges);
    }

    [Fact]
    public void Provenance_edges_for_agent_gc_and_messages()
    {
        var revision = Project("", Note(origin: new MemoryOrigin("gc-1", ["msg-1", "msg-2"])));

        Assert.Contains(new MemoryGraphEdge("agent:" + Agent, MemoryEdgeKind.AuthoredBy, 0.0), revision.Edges);
        Assert.Contains(new MemoryGraphEdge("gc:gc-1", MemoryEdgeKind.FromGc, 0.0), revision.Edges);
        Assert.Contains(new MemoryGraphEdge("message:msg-1", MemoryEdgeKind.FromMessage, 0.0), revision.Edges);
        Assert.Contains(new MemoryGraphEdge("message:msg-2", MemoryEdgeKind.FromMessage, 0.0), revision.Edges);
    }

    [Fact]
    public void Null_or_empty_gc_produces_no_gc_edge()
    {
        var withNullGc = Project("", Note(origin: new MemoryOrigin(null, [])));
        var withEmptyGc = Project("", Note(origin: new MemoryOrigin("", [])));
        var noOrigin = Project("", Note(origin: null));

        foreach (var revision in new[] { withNullGc, withEmptyGc, noOrigin })
        {
            Assert.DoesNotContain(revision.Edges, e => e.Kind == MemoryEdgeKind.FromGc);
            Assert.DoesNotContain(revision.Edges, e => e.Kind == MemoryEdgeKind.FromMessage);
        }
    }

    [Fact]
    public void Duplicate_targets_of_the_same_kind_are_deduplicated()
    {
        var revision = Project("", Note(derivedFrom: [OtherId, OtherId]));

        Assert.Single(revision.Edges, e => e.Kind == MemoryEdgeKind.DerivedFrom);
    }

    [Fact]
    public void Same_target_under_different_kinds_is_kept_twice()
    {
        var revision = Project("[[Other note]]", Note(derivedFrom: [OtherId]));

        Assert.Contains(new MemoryGraphEdge(OtherId, MemoryEdgeKind.Link, 1.0), revision.Edges);
        Assert.Contains(new MemoryGraphEdge(OtherId, MemoryEdgeKind.DerivedFrom, 0.8), revision.Edges);
    }

    [Fact]
    public void Edges_are_ordered_by_kind_then_target()
    {
        var note = Note(
            derivedFrom: ["zeta", "alpha"],
            supersedes: ["mid"],
            origin: new MemoryOrigin("gc-b", ["msg-z", "msg-a"]));

        var revision = Project("[[Third note]] [[Other note]]", note);

        var kinds = revision.Edges.Select(e => e.Kind).ToList();
        Assert.Equal(kinds.OrderBy(k => (int)k).ToList(), kinds);

        var linkTargets = revision.Edges.Where(e => e.Kind == MemoryEdgeKind.Link).Select(e => e.TargetId).ToList();
        Assert.Equal(linkTargets.OrderBy(t => t, StringComparer.Ordinal).ToList(), linkTargets);

        var fromMessage = revision.Edges.Where(e => e.Kind == MemoryEdgeKind.FromMessage).Select(e => e.TargetId).ToList();
        Assert.Equal(["message:msg-a", "message:msg-z"], fromMessage);
    }

    [Fact]
    public void Same_input_gives_the_same_output_whatever_the_order_of_the_title_map()
    {
        var reversed = new Dictionary<string, string>
        {
            ["Self note"] = SelfId,
            ["Third note"] = ThirdId,
            ["Other note"] = OtherId,
        };
        var body = "[[Other note]] [[Third note]] [[Other note]]";

        var first = MemoryGraphProjector.Project(Note(), body, Path, Agent, Titles);
        var second = MemoryGraphProjector.Project(Note(), body, Path, Agent, reversed);

        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal(first.Title, second.Title);
    }

    [Fact]
    public void Null_note_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MemoryGraphProjector.Project(null!, "", Path, Agent, Titles));
    }

    [Fact]
    public void Null_body_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MemoryGraphProjector.Project(Note(), null!, Path, Agent, Titles));
    }

    [Fact]
    public void Null_title_map_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MemoryGraphProjector.Project(Note(), "", Path, Agent, null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_vault_path_throws(string? vaultPath)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            MemoryGraphProjector.Project(Note(), "", vaultPath!, Agent, Titles));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_scope_agent_throws(string? agentId)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            MemoryGraphProjector.Project(Note(), "", Path, agentId!, Titles));
    }
}
