using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>The pure memory graph projection: links, provenance, ordering and validation. No database needed.</summary>
public sealed class MemoryGraphProjectorTests
{
    private const string _path = "agents/alpha/memory/lessons/retry-backoff.md";
    private const string _agent = "alpha";
    private const string _selfId = "01SELF";
    private const string _otherId = "01OTHER";
    private const string _thirdId = "01THIRD";

    private static readonly IReadOnlyDictionary<string, string> _titles = new Dictionary<string, string>
    {
        ["Other note"] = _otherId,
        ["Third note"] = _thirdId,
        ["Self note"] = _selfId,
    };

    private static MemoryNote Note(
        IReadOnlyList<string>? derivedFrom = null,
        IReadOnlyList<string>? supersedes = null,
        MemoryOrigin? origin = null,
        long version = 3) =>
        new(
            Id: _selfId,
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
        MemoryGraphProjector.Project(note ?? Note(), body, _path, _agent, _titles);

    [Fact]
    public void Revision_fields_come_from_the_note_and_arguments()
    {
        var revision = Project("", Note(version: 7));

        Assert.Equal(_selfId, revision.NoteId);
        Assert.Equal(7, revision.Version);
        Assert.Equal(_agent, revision.ScopeAgentId);
        Assert.Equal(_path, revision.VaultPath);
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
        Assert.Equal(new MemoryGraphEdge("agent:" + _agent, MemoryEdgeKind.AuthoredBy, 0.0), edge);
    }

    [Fact]
    public void Wiki_link_resolves_by_title()
    {
        var revision = Project("See [[Other note]] for details.");

        Assert.Contains(new MemoryGraphEdge(_otherId, MemoryEdgeKind.Link, 1.0), revision.Edges);
    }

    [Fact]
    public void Wiki_link_resolves_by_note_id()
    {
        var revision = Project($"Builds on [[{_thirdId}]].");

        Assert.Contains(new MemoryGraphEdge(_thirdId, MemoryEdgeKind.Link, 1.0), revision.Edges);
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
        Assert.Equal(new MemoryGraphEdge(_otherId, MemoryEdgeKind.Link, 1.0), link);
    }

    [Fact]
    public void Whitespace_inside_brackets_is_trimmed()
    {
        var revision = Project("See [[  Third note  ]].");

        Assert.Contains(new MemoryGraphEdge(_thirdId, MemoryEdgeKind.Link, 1.0), revision.Edges);
    }

    [Fact]
    public void Self_link_by_title_or_id_produces_no_edge()
    {
        var revision = Project($"[[Self note]] and [[{_selfId}]].");

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
        var revision = Project("", Note(derivedFrom: [_otherId], supersedes: [_thirdId]));

        Assert.Contains(new MemoryGraphEdge(_otherId, MemoryEdgeKind.DerivedFrom, 0.8), revision.Edges);
        Assert.Contains(new MemoryGraphEdge(_thirdId, MemoryEdgeKind.Supersedes, 0.5), revision.Edges);
    }

    [Fact]
    public void Provenance_edges_for_agent_gc_and_messages()
    {
        var revision = Project("", Note(origin: new MemoryOrigin("gc-1", ["msg-1", "msg-2"])));

        Assert.Contains(new MemoryGraphEdge("agent:" + _agent, MemoryEdgeKind.AuthoredBy, 0.0), revision.Edges);
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
        var revision = Project("", Note(derivedFrom: [_otherId, _otherId]));

        Assert.Single(revision.Edges, e => e.Kind == MemoryEdgeKind.DerivedFrom);
    }

    [Fact]
    public void Same_target_under_different_kinds_is_kept_twice()
    {
        var revision = Project("[[Other note]]", Note(derivedFrom: [_otherId]));

        Assert.Contains(new MemoryGraphEdge(_otherId, MemoryEdgeKind.Link, 1.0), revision.Edges);
        Assert.Contains(new MemoryGraphEdge(_otherId, MemoryEdgeKind.DerivedFrom, 0.8), revision.Edges);
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
            ["Self note"] = _selfId,
            ["Third note"] = _thirdId,
            ["Other note"] = _otherId,
        };
        var body = "[[Other note]] [[Third note]] [[Other note]]";

        var first = MemoryGraphProjector.Project(Note(), body, _path, _agent, _titles);
        var second = MemoryGraphProjector.Project(Note(), body, _path, _agent, reversed);

        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal(first.Title, second.Title);
    }

    [Fact]
    public void Null_note_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MemoryGraphProjector.Project(null!, "", _path, _agent, _titles));
    }

    [Fact]
    public void Null_body_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MemoryGraphProjector.Project(Note(), null!, _path, _agent, _titles));
    }

    [Fact]
    public void Null_title_map_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MemoryGraphProjector.Project(Note(), "", _path, _agent, null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_vault_path_throws(string? vaultPath)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            MemoryGraphProjector.Project(Note(), "", vaultPath!, _agent, _titles));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_scope_agent_throws(string? agentId)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            MemoryGraphProjector.Project(Note(), "", _path, agentId!, _titles));
    }
}
